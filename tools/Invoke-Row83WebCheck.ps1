#Requires -Version 7
<#
.SYNOPSIS
    Live check: a Claude spawn in a directory room searches and fetches the web, and the room commit
    lists each call; a fetch of the hub's loopback address is denied.

.DESCRIPTION
    Spends one model call on the owner's Claude subscription. Starts the given hub exe against fresh
    folders under %TEMP% (--data, --port and --rooms-root; never a real data or rooms folder), creates
    a directory room, and posts one owner message with a leading mention, so only that participant
    spawns. Outside a run the spawn works in a worktree that is merged --no-ff when the exchange ends,
    so the agent's own commit is read at HEAD^2 after the merge note.

    Ends with `Results: n/m PASS`. Exit 0 when every check passes, 1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$Participant = 'sonnet',
    [int]$TimeoutSeconds = 420
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$scratch = Join-Path $env:TEMP ('chopitup_webcheck_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$tokens = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
$owner = New-ChopBearerHeaders -Token $tokens.owner
$checks = [System.Collections.Generic.List[bool]]::new()
$hub = $null

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $checks.Add($Passed)
    Write-Host ("{0,-5} {1}  {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail).TrimEnd()
}

function Wait-Message([string]$RoomId, [scriptblock]$Match) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $page = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?limit=200" -TimeoutSec 10
        $hit = @($page.messages | Where-Object $Match) | Select-Object -First 1
        if ($hit) { return $hit }
        Start-Sleep -Seconds 3
    }
    return $null
}

try {
    New-Item -ItemType Directory -Force $roomsRoot | Out-Null
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--rooms-root', "`"$roomsRoot`"") `
        -WindowStyle Hidden -PassThru -RedirectStandardError "$scratch\hub.err.log" -RedirectStandardOutput "$scratch\hub.out.log"
    $up = $false
    foreach ($i in 1..60) { try { Invoke-RestMethod "$base/health" -TimeoutSec 2 | Out-Null; $up = $true; break } catch { Start-Sleep -Milliseconds 500 } }
    if (-not $up) { throw 'scratch hub never answered /health' }

    $room = Invoke-RestMethod -Method Post -Uri "$base/api/rooms" -Headers $owner -ContentType 'application/json' -Body '{"name":"Web Check"}' -TimeoutSec 60
    $body = "@$Participant Use the WebSearch tool once to search for `"IANA example domain`". Then use the WebFetch tool once on https://example.com/ and once on $base/health. Reply with the example.com page title and what happened to the second fetch."
    $posted = Invoke-RestMethod -Method Post -Uri "$base/api/rooms/$($room.id)/messages" -Headers $owner -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json) -TimeoutSec 15

    $note = Wait-Message $room.id { $_.authorId -eq 'hub' -and $_.body -like 'Committed *' }
    Add-Check 'note.counts-web-requests' ($null -ne $note -and $note.body -match '\d+ web request\(s\)\.') "$($note.body)"
    $merged = Wait-Message $room.id { $_.authorId -eq 'hub' -and $_.body -like "Exchange #$($posted.id) merged into*" }
    Add-Check 'exchange.merged' ($null -ne $merged)

    $commit = (& git -C $room.directory log -1 --format=%B 'HEAD^2' 2>&1 | Out-String) -replace "`r`n", "`n"
    Add-Check 'commit.web-header' ($commit -match '(?m)^Web requests \(\d+\):$')
    Add-Check 'commit.websearch' ($commit -match '(?m)^  \d+\. WebSearch: .+$')
    Add-Check 'commit.webfetch-example' ($commit -match '(?m)^  \d+\. WebFetch: https://example\.com/?$')
    Add-Check 'commit.loopback-fetch-denied' ($commit -match "(?m)^  \d+\. WebFetch: http://127\.0\.0\.1:$port/health \[denied\]$")
    Add-Check 'commit.no-token' (-not $commit.Contains($tokens.owner))
    Write-Host ($commit -split "`n" | Where-Object { $_ -match 'Web|WebSearch|WebFetch|Shell' } | ForEach-Object { "  | $_" }) -Separator "`n"
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    if ($hub -and -not $hub.HasExited) { $hub.Kill($true); $hub.WaitForExit(10000) | Out-Null }
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

$passed = @($checks | Where-Object { $_ }).Count
Write-Host "Results: $passed/$($checks.Count) PASS"
exit ($(if ($passed -eq $checks.Count) { 0 } else { 1 }))
