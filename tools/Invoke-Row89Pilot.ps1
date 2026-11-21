#Requires -Version 7
<#
.SYNOPSIS
    The goal pilot: one goal run through the real desktop shell, a scratch hub and the real CLIs, with
    a GPU background job against a fake local render service, ending in a presentation with images
    and a run notification.

.DESCRIPTION
    Spends model calls on the owner's Claude and Codex subscriptions (a conductor, two builders and
    two judges, about ten to fifteen spawns). Fresh folders under %TEMP% only: the shell's --data, the
    rooms root (CHOPITUP_ROOMS) and the fake service's output. The shell starts its own hub there and
    inherits this process's PATH, so the real CLIs are found.

    tools/fixtures/Start-FakeRenderService.ps1 runs on a free port: a render takes -RenderSeconds and
    returns a PNG, and /interrupt stops one. The goal asks for a poster rendered through that service
    from a background job with gpu: true, so the job holds the machine's GPU lock while it runs.

    Checks, from the hub's HTTP API, the room directory, the fake service's /stats and the shell's own
    desktop.log: the run ended on the conductor's ping within its budget; GOAL.md exists; the conductor
    posted plan, build, critique and ping phases; each builder and each judge posted; a job exited 0
    and the service rendered with no overlap; the ping shows artifact: images the file route serves;
    the shell showed one presented notification. With Windows notifications off for the user, Windows
    draws nothing and the script says so.

    The transcript, GOAL.md and the images are copied to -EvidenceDir. Ends with `Results: n/m PASS`.
#>
[CmdletBinding()]
param(
    [string]$ShellExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Desktop\bin\Debug\net10.0-windows\ChopItUp.Desktop.exe'),
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$SkillDir = (Join-Path $PSScriptRoot 'skills\goal'),
    [string]$EvidenceDir = (Join-Path $env:TEMP ('chopitup_pilot_evidence_' + [guid]::NewGuid().ToString('N'))),
    [int]$RenderSeconds = 45,
    [int]$TimeoutMinutes = 120
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$ShellExe = (Resolve-Path -LiteralPath $ShellExe).Path
$HubExe = (Resolve-Path -LiteralPath $HubExe).Path
$SkillDir = (Resolve-Path -LiteralPath $SkillDir).Path
$scratch = Join-Path $env:TEMP ('chopitup_pilot_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"
New-Item -ItemType Directory -Force $roomsRoot, $EvidenceDir | Out-Null
function Get-FreePort { $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }
$port = Get-FreePort; $renderPort = Get-FreePort
$base = "http://127.0.0.1:$port"
$owner = New-ChopBearerHeaders -Token (Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')).owner
$log = "$dataDir\logs\desktop.log"
$fails = 0; $checks = 0
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks++
    if (-not $Passed) { $script:fails++ }
    Write-Host ("{0,-5} {1}  {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail).TrimEnd()
}
function Get-LogCount([string]$Line) { if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log | Where-Object { $_ -like "*$Line*" }).Count } else { 0 } }
function Get-Messages([string]$Room) { @((Invoke-RestMethod "$base/api/rooms/$Room/messages?afterId=0&limit=500" -TimeoutSec 30).messages) }

$goal = "Make a poster image for 'Chop It Up' with the local GPU render service at http://127.0.0.1:$renderPort. " +
    "POST /render?prompt=<text> answers with a PNG after about a minute, and POST /interrupt stops a render in progress. " +
    "Call the service only from a background job started with start_job and gpu: true, with a cancel_command that posts to /interrupt, " +
    "and save the PNG in the artifacts folder under renders/. Then make one small caption card of your own as a second PNG. Present both images."

try {
    $service = Start-Process pwsh -ArgumentList @('-NoProfile', '-File', "`"$(Join-Path $PSScriptRoot 'fixtures\Start-FakeRenderService.ps1')`"", '-Port', "$renderPort", '-RenderSeconds', "$RenderSeconds") -WindowStyle Hidden -PassThru
    $started.Add($service)
    $import = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--import-skill', "`"$SkillDir`"") -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput "$scratch\import.out.log" -RedirectStandardError "$scratch\import.err.log"
    Add-Check 'skill.imported' ($import.ExitCode -eq 0) "exit=$($import.ExitCode)"

    $savedRooms = $env:CHOPITUP_ROOMS; $env:CHOPITUP_ROOMS = $roomsRoot
    try { $shell = Start-Process -FilePath $ShellExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--hub', "`"$HubExe`"") -PassThru }
    finally { $env:CHOPITUP_ROOMS = $savedRooms }
    $started.Add($shell)
    $health = $null
    foreach ($i in 1..80) { try { $health = Invoke-RestMethod "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 } }
    if (-not $health) { throw 'the shell''s hub never answered /health' }

    $room = Invoke-RestMethod -Uri "$base/api/rooms" -Method Post -Headers $owner -ContentType 'application/json' -Body (@{ name = 'Pilot' } | ConvertTo-Json) -TimeoutSec 60
    Invoke-RestMethod -Uri "$base/api/rooms/$($room.id)/messages" -Method Post -Headers $owner -ContentType 'application/json' `
        -Body (@{ body = "/goal @opus hours: 2 $goal" } | ConvertTo-Json) -TimeoutSec 30 | Out-Null

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes); $seen = 0; $run = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 20
        $messages = Get-Messages $room.id
        foreach ($m in $messages | Select-Object -Skip $seen) {
            $first = ($m.body -split "`n")[0]
            Write-Host ("  [{0}] {1}" -f $m.authorId, $first.Substring(0, [Math]::Min(100, $first.Length)))
        }
        $seen = $messages.Count
        try { $run = Invoke-RestMethod "$base/api/rooms/$($room.id)/run" -TimeoutSec 10 } catch { $run = $null }
        if ($run -and $run.status -in 'ended', 'parked') { break }
    }
    Add-Check 'run.ended-on-ping-within-budget' ($run.status -eq 'ended' -and $run.reason -eq 'the conductor pinged' -and $run.spawnsUsed -le $run.spawnCap) `
        "status=$($run.status) reason=$($run.reason) spawns=$($run.spawnsUsed)/$($run.spawnCap) minutes=$($run.elapsedMinutes)/$($run.wallClockCapMinutes)"

    $messages = Get-Messages $room.id
    $messages | ForEach-Object { "### [$($_.authorId)] #$($_.id)`n$($_.body)`n" } | Set-Content -LiteralPath "$EvidenceDir\transcript.md" -Encoding utf8NoBOM
    $goalMd = Join-Path $room.directory 'GOAL.md'
    if (Test-Path -LiteralPath $goalMd) { Copy-Item -LiteralPath $goalMd "$EvidenceDir\GOAL.md" }
    Add-Check 'step2.goal-md' (Test-Path -LiteralPath $goalMd)

    $conductor = @($messages | Where-Object { $_.authorId -eq 'opus' })
    foreach ($kind in 'plan', 'build', 'critique', 'ping') { Add-Check "phase.$kind" (@($conductor | Where-Object { $_.body -match "^phase: $kind(\b|/)" }).Count -ge 1) }
    foreach ($id in 'gpt-6-astra', 'sonnet', 'opus-judge', 'gpt-5.6-sol') { Add-Check "posted.$id" (@($messages | Where-Object { $_.authorId -eq $id }).Count -ge 1) }

    $jobEnds = @($messages | Where-Object { $_.authorId -eq 'hub' -and $_.body -match '^Job j\d+ by @\S+ exited with code 0\.' })
    $stats = Invoke-RestMethod "http://127.0.0.1:$renderPort/stats" -TimeoutSec 10
    Add-Check 'step3.gpu-job-rendered' ($jobEnds.Count -ge 1 -and $stats.renders -ge 1 -and $stats.maxConcurrent -le 1) "jobsExited0=$($jobEnds.Count) renders=$($stats.renders) maxConcurrent=$($stats.maxConcurrent)"

    $ping = @($conductor | Where-Object { $_.body -match '^phase: ping' }) | Select-Object -Last 1
    $refs = if ($ping) { @([regex]::Matches($ping.body, '!\[[^\]]*\]\((artifact:[^)\s]+)\)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique) } else { @() }
    $served = 0; $i = 0
    foreach ($ref in $refs) {
        $file = Invoke-WebRequest "$base/api/rooms/$($room.id)/files?ref=$([uri]::EscapeDataString($ref))" -TimeoutSec 30 -SkipHttpErrorCheck
        if ($file.StatusCode -eq 200 -and "$($file.Headers['Content-Type'])" -like 'image/*') {
            $served++; $i++
            [IO.File]::WriteAllBytes("$EvidenceDir\image-$i$([IO.Path]::GetExtension($ref))", $file.Content)
        }
    }
    Add-Check 'step5.images-inline-and-served' ($refs.Count -ge 2 -and $served -eq $refs.Count) "images=$($refs.Count) served=$served"

    Add-Check 'toast.presented-shown' ((Get-LogCount 'NOTICE shown kind=presented run=1') -eq 1)
    if ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' -ErrorAction SilentlyContinue).ToastEnabled -eq 0) {
        $script:skipped = 'toast.on-screen: Windows notifications are off for this user (ToastEnabled=0), so Windows drew nothing'
    }
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    try { Start-Process -FilePath $ShellExe -ArgumentList @('--data', "`"$dataDir`"", '--quit') -Wait -NoNewWindow | Out-Null } catch { }
    foreach ($p in $started) { if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit(10000) | Out-Null } }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:skipped) { Write-Host "SKIP  $script:skipped" }
Write-Host "Evidence: $EvidenceDir"
Write-Host "Results: $($checks - $fails)/$checks PASS"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
