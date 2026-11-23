<#
.SYNOPSIS
    Artifacts folder live check: real Claude and Codex spawns write through CHOPITUP_ARTIFACTS, in a
    worktree exchange and in a run, and the files stay out of the room's git history.

.DESCRIPTION
    Spends four real model turns on the local subscriptions: -Claude (default sonnet) and -Codex
    (default gpt-5.6-terra), each once in a worktree exchange and once as the conductor of the
    artifacts-run fixture skill. Never touches C:\Self Apps, %USERPROFILE%\ChopItUp or any real data
    directory: -DataDir and -RoomsRoot default to fresh folders under $env:TEMP and are left behind
    with the log.

    Each of the four scenarios makes three checks, so a complete pass is exactly 12:
      file-written   the file holds the expected line once the exchange closed, or once a run posted
                     its first commit note. A run must also record it as artifact:run-output.txt,
                     credited to the conductor.
      room-clean     git status in the room directory prints nothing.
      not-in-history no commit on any ref in the room lists the file.

    A file-written FAIL can mean that the model did not follow the instruction, so its detail names
    the room's last message. The hub is stopped by PID, always. The last line is "Results: n/12 PASS".
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_artifactscheck_' + [guid]::NewGuid().ToString('N'))),
    [string]$RoomsRoot = '',
    [int]$Port = 8879,
    [int]$ScenarioSeconds = 420,
    [string]$SkillSource = (Join-Path $PSScriptRoot 'skills\artifacts-run'),
    [string]$Claude = 'sonnet',
    [string]$Codex = 'gpt-5.6-terra'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
if (-not $RoomsRoot) { $RoomsRoot = "$DataDir.rooms" }
$ExpectedChecks = 12
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.artifacts-check.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $line = ('{0}  {1}  {2}' -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $SkillSource 'SKILL.md') -PathType Leaf)) {
    Write-Error "No SKILL.md under '$SkillSource'." -ErrorAction Continue
    exit 2
}
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    Write-Error "Port $Port is already in use. Pass -Port." -ErrorAction Continue
    exit 2
}
foreach ($d in @($DataDir, $RoomsRoot)) {
    if (Test-Path -LiteralPath $d) {
        Write-Error "'$d' already exists; this script only runs against fresh directories." -ErrorAction Continue
        exit 2
    }
}
New-Item -ItemType Directory -Path $DataDir | Out-Null
$ownerToken = (Initialize-ChopScratchTokens -DataDir $DataDir -ParticipantIds @('owner')).owner
$base = "http://127.0.0.1:$Port"
$hub = $null

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $request = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30; Headers = (New-ChopBearerHeaders -Token $ownerToken) }
    if ($null -ne $Body) { $request.ContentType = 'application/json'; $request.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @request
}
function Get-Messages([string]$RoomId) {
    try { @((Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?afterId=0&limit=500" -TimeoutSec 10).messages) }
    catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
}
# Waits for a hub note, because the hub commits a spawn's work after the spawn exits, and closes a
# worktree after that.
function Wait-HubNote([string]$RoomId, [string]$Pattern, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $note = Get-Messages -RoomId $RoomId | Where-Object { $_.authorId -eq 'hub' -and $_.body -match $Pattern } | Select-Object -First 1
        if ($note) { return $note }
        Start-Sleep -Seconds 3
    }
    return $null
}
function Get-LastMessage([string]$RoomId) {
    $last = Get-Messages -RoomId $RoomId | Select-Object -Last 1
    if ($last) { "last message by @$($last.authorId): $($last.body.Split("`n")[0])" } else { 'room has no messages' }
}
function Add-GitChecks([string]$Scenario, [string]$Directory, [string]$FileName) {
    $status = & git -C $Directory status --porcelain 2>&1
    $statusExit = $LASTEXITCODE
    Add-Check -Name "$Scenario.room-clean" -Passed ($statusExit -eq 0 -and -not $status) -Detail "exit=$statusExit status=[$($status -join ' ')]"

    $listed = @(& git -C $Directory log --all --name-only --format= 2>&1)
    $logExit = $LASTEXITCODE
    Add-Check -Name "$Scenario.not-in-history" -Passed ($logExit -eq 0 -and $listed -notcontains $FileName) -Detail "exit=$logExit filesListed=$($listed.Count)"
}
function Test-FileHolds([string]$Path, [string]$Line) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and ((Get-Content -LiteralPath $Path -Raw) -match [regex]::Escape($Line))
}

function Invoke-ExchangeScenario([string]$Scenario, [string]$Participant) {
    $room = Invoke-Api POST '/api/rooms' @{ name = "Exchange $Participant" }
    $fileName = "exchange-$Participant.txt"
    $line = 'artifacts folder check'
    $body = "@$Participant Run one shell command that writes the single line ``$line`` to a file named ``$fileName`` in the folder named by the CHOPITUP_ARTIFACTS environment variable. Write nothing else anywhere. Then post one short line saying it is done."
    Invoke-Api POST "/api/rooms/$($room.id)/messages" @{ body = $body } | Out-Null

    $closed = Wait-HubNote -RoomId $room.id -Pattern '^Exchange #\d+ merged into' -Seconds $ScenarioSeconds
    $path = Join-Path "$($room.directory).artifacts" $fileName
    $written = $null -ne $closed -and (Test-FileHolds -Path $path -Line $line)
    Add-Check -Name "$Scenario.file-written" -Passed $written -Detail "closed=$($null -ne $closed) file=$path; $(Get-LastMessage -RoomId $room.id)"
    Add-GitChecks -Scenario $Scenario -Directory $room.directory -FileName $fileName
}

function Invoke-RunScenario([string]$Scenario, [string]$Conductor) {
    $room = Invoke-Api POST '/api/rooms' @{ name = "Run $Conductor" }
    $fileName = 'run-output.txt'
    Invoke-Api POST "/api/rooms/$($room.id)/messages" @{ body = "/artifacts-run @$Conductor" } | Out-Null

    $committed = Wait-HubNote -RoomId $room.id -Pattern '^Committed ' -Seconds $ScenarioSeconds
    $run = $null
    try { $run = Invoke-RestMethod -Uri "$base/api/rooms/$($room.id)/run" -TimeoutSec 10 } catch { Add-Content -Path $log -Value "read run failed: $($_.Exception.Message)" }
    $recorded = @($run.artifacts | Where-Object { $_.path -eq "artifact:$fileName" -and $_.authorId -eq $Conductor })
    $path = Join-Path "$($room.directory).artifacts" $fileName
    $written = $null -ne $committed -and $recorded.Count -eq 1 -and (Test-FileHolds -Path $path -Line 'artifacts folder check')
    Add-Check -Name "$Scenario.file-written" -Passed $written `
        -Detail "committed=$($null -ne $committed) status=$($run.status) artifacts=$(($run.artifacts | ForEach-Object { "$($_.path):$($_.authorId)" }) -join ' | '); $(Get-LastMessage -RoomId $room.id)"
    Add-GitChecks -Scenario $Scenario -Directory $room.directory -FileName $fileName
}

# Paths are quoted inside the argument string because Start-Process joins -ArgumentList with spaces.
$import = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--import-skill', "`"$SkillSource`"") -PassThru -Wait -NoNewWindow `
    -RedirectStandardOutput (Join-Path $DataDir 'import.out.log') -RedirectStandardError (Join-Path $DataDir 'import.err.log')
if ($import.ExitCode -ne 0) {
    Write-Error "Importing '$SkillSource' failed (exit $($import.ExitCode))." -ErrorAction Continue
    exit 2
}

try {
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port", '--rooms-root', "`"$RoomsRoot`"") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($null -eq $health) { throw "Hub never answered /health on pid $($hub.Id)." }

    Invoke-ExchangeScenario -Scenario 'exchange.claude' -Participant $Claude
    Invoke-ExchangeScenario -Scenario 'exchange.codex' -Participant $Codex
    Invoke-RunScenario -Scenario 'run.claude' -Conductor $Claude
    Invoke-RunScenario -Scenario 'run.codex' -Conductor $Codex
}
catch {
    Write-Host "Check stopped early: $($_.Exception.Message)"
    Add-Content -Path $log -Value "stopped early: $($_.Exception.Message)"
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $orphans = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $PID })
    foreach ($p in $orphans) { Write-Host "orphan from this check, stopping pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }

    $passed = @($script:Checks | Where-Object Passed).Count
    Write-Host "Log: $log"
    $line = "Results: $passed/$ExpectedChecks PASS"
    Add-Content -Path $log -Value $line
    Write-Host $line
}
exit $(if ($passed -eq $ExpectedChecks -and $script:Checks.Count -eq $ExpectedChecks) { 0 } else { 1 })
