#Requires -Version 7
<#
.SYNOPSIS
    Live check of the goal skill: one goal run on a scratch hub with the real CLIs, from plan to a
    presentation with an inline image.

.DESCRIPTION
    Spends model calls on the owner's Claude and Codex subscriptions: a conductor, two builders and two
    judges, about ten spawns. Starts the given hub exe against fresh folders under %TEMP% (--data,
    --port and --rooms-root; never a real data or rooms folder), imports tools/skills/goal with the
    hub's own --import-skill, creates a directory room and posts `/goal @opus hours: 2 <goal>` with a
    small fixture goal that needs no GPU.

    It waits for the run to end, then checks: the run ended on the conductor's ping; GOAL.md has its
    Rubric and Done when sections; the conductor posted plan, build, critique and ping phases; each
    builder and each judge posted; and the ping shows an inline artifact: image that the room file
    route serves as an image.

    The transcript, GOAL.md and the image are copied to -EvidenceDir for judging. Ends with
    `Results: n/m PASS`. Exit 0 when every check passes, 1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$SkillDir = (Join-Path $PSScriptRoot 'skills\goal'),
    [string]$EvidenceDir = (Join-Path $env:TEMP ('chopitup_goalcheck_evidence_' + [guid]::NewGuid().ToString('N'))),
    [int]$TimeoutMinutes = 100,
    [string]$Goal = 'Make one 512 by 512 PNG badge that reads CHOP IT UP in bold letters on a dark background, drawn by a script in this room. That one image is the whole deliverable.'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$HubExe = (Resolve-Path -LiteralPath $HubExe).Path
$SkillDir = (Resolve-Path -LiteralPath $SkillDir).Path
$scratch = Join-Path $env:TEMP ('chopitup_goalcheck_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"
New-Item -ItemType Directory -Force $roomsRoot, $EvidenceDir | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$tokens = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
$owner = New-ChopBearerHeaders -Token $tokens.owner
$fails = 0; $checks = 0; $hub = $null

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks++
    if (-not $Passed) { $script:fails++ }
    Write-Host ("{0,-5} {1}  {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail).TrimEnd()
}

function Get-Messages([string]$Room) { @((Invoke-RestMethod "$base/api/rooms/$Room/messages?afterId=0&limit=500" -TimeoutSec 30).messages) }

try {
    $import = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--import-skill', "`"$SkillDir`"") -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput "$scratch\import.out.log" -RedirectStandardError "$scratch\import.err.log"
    Add-Check 'skill.imported' ($import.ExitCode -eq 0) "exit=$($import.ExitCode)"

    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--rooms-root', "`"$roomsRoot`"") `
        -WindowStyle Hidden -PassThru -RedirectStandardError "$scratch\hub.err.log" -RedirectStandardOutput "$scratch\hub.out.log"
    $health = $null
    foreach ($i in 1..60) { try { $health = Invoke-RestMethod "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 } }
    if (-not $health) { throw 'scratch hub never answered /health' }

    $room = Invoke-RestMethod -Uri "$base/api/rooms" -Method Post -Headers $owner -ContentType 'application/json' -Body (@{ name = 'Goal Lab' } | ConvertTo-Json) -TimeoutSec 60
    Add-Check 'room.directory' ([bool]$room.directory) "room=$($room.id)"
    Invoke-RestMethod -Uri "$base/api/rooms/$($room.id)/messages" -Method Post -Headers $owner -ContentType 'application/json' `
        -Body (@{ body = "/goal @opus hours: 2 $Goal" } | ConvertTo-Json) -TimeoutSec 30 | Out-Null

    # Wait for the run to end or park, reporting each new post as it lands.
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $seen = 0; $run = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 20
        $messages = Get-Messages $room.id
        foreach ($m in $messages | Select-Object -Skip $seen) {
            Write-Host ("  [{0}] {1}" -f $m.authorId, (($m.body -split "`n")[0]).Substring(0, [Math]::Min(100, (($m.body -split "`n")[0]).Length)))
        }
        $seen = $messages.Count
        try { $run = Invoke-RestMethod "$base/api/rooms/$($room.id)/run" -TimeoutSec 10 } catch { $run = $null }
        if ($run -and $run.status -in 'ended', 'parked') { break }
    }
    Add-Check 'run.ended-on-ping' ($run.status -eq 'ended' -and $run.reason -eq 'the conductor pinged') "status=$($run.status) reason=$($run.reason) spawns=$($run.spawnsUsed)"

    $messages = Get-Messages $room.id
    $messages | ForEach-Object { "### [$($_.authorId)] #$($_.id)`n$($_.body)`n" } | Set-Content -LiteralPath "$EvidenceDir\transcript.md" -Encoding utf8NoBOM
    $goalMd = Join-Path $room.directory 'GOAL.md'
    $hasGoal = Test-Path -LiteralPath $goalMd
    if ($hasGoal) { Copy-Item -LiteralPath $goalMd "$EvidenceDir\GOAL.md" }
    $goalText = if ($hasGoal) { Get-Content -LiteralPath $goalMd -Raw } else { '' }
    Add-Check 'goal-md.sections' ($hasGoal -and $goalText -match '(?im)^#+.*Rubric' -and $goalText -match '(?im)^#+.*Done when')

    $conductor = @($messages | Where-Object { $_.authorId -eq 'opus' })
    foreach ($kind in 'plan', 'build', 'critique', 'ping') {
        Add-Check "phase.$kind" (@($conductor | Where-Object { $_.body -match "^phase: $kind(\b|/)" }).Count -ge 1)
    }
    foreach ($id in 'gpt-6-astra', 'sonnet', 'opus-judge', 'gpt-5.6-sol') {
        Add-Check "posted.$id" (@($messages | Where-Object { $_.authorId -eq $id }).Count -ge 1)
    }

    $ping = @($conductor | Where-Object { $_.body -match '^phase: ping' }) | Select-Object -Last 1
    $ref = if ($ping -and $ping.body -match '!\[[^\]]*\]\((artifact:[^)\s]+)\)') { $Matches[1] } else { $null }
    Add-Check 'ping.inline-image' ([bool]$ref) "ref=$ref"
    if ($ref) {
        $file = Invoke-WebRequest "$base/api/rooms/$($room.id)/files?ref=$([uri]::EscapeDataString($ref))" -TimeoutSec 30 -SkipHttpErrorCheck
        Add-Check 'ping.image-served' ($file.StatusCode -eq 200 -and "$($file.Headers['Content-Type'])" -like 'image/*') "status=$($file.StatusCode) type=$($file.Headers['Content-Type'])"
        if ($file.StatusCode -eq 200) { [IO.File]::WriteAllBytes("$EvidenceDir\ping-image$([IO.Path]::GetExtension($ref))", $file.Content) }
    }
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    if ($hub -and -not $hub.HasExited) { $hub.Kill($true); $hub.WaitForExit(10000) | Out-Null }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Evidence: $EvidenceDir"
Write-Host "Results: $($checks - $fails)/$checks PASS"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
