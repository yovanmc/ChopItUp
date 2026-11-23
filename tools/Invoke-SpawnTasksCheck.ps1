#Requires -Version 7
<#
.SYNOPSIS
    Spawn task live check (real Sonnet, one call, spends): one real Claude spawn through a scratch hub
    produces one finished task row that the tasks route reports.

.DESCRIPTION
    Starts the given hub on a fresh scratch data folder under %TEMP% with the real claude CLI on PATH,
    posts one owner message to @sonnet in the plain general room, waits for the exchange to conclude,
    and reads GET /api/rooms/general/tasks while the hub still runs:
      - exactly one task, whose spawn id equals the id on the hub's "spawn <id>: sonnet starting" line
      - requested model sonnet, host claude, retry false, no effort flag
      - termination completed, exit code 0, a positive duration, one post
      - token numbers present (input and output) and a reported model naming Sonnet
      - a tool call count of at least 1 (its post_message call), read from the stream-json events

    No real data folder or rooms folder is read or written. The last line is "Results: n/m PASS".
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$latestSchema = & (Join-Path $PSScriptRoot 'Get-ChopSchemaVersion.ps1')
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$HubExe = (Resolve-Path -LiteralPath $HubExe).Path
$scratch = Join-Path $env:TEMP ('chopitup_taskscheck_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"
New-Item -ItemType Directory -Force $roomsRoot | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$ownerToken = (Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')).owner
$ownerAuth = New-ChopBearerHeaders -Token $ownerToken
$fails = 0
$checks = 0
$hub = $null

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks++
    if (-not $Passed) { $script:fails++ }
    Write-Host ("{0,-5} {1}  {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail).TrimEnd()
}

try {
    Add-Check 'cli.claude-on-path' ([bool](Get-Command claude -ErrorAction SilentlyContinue))
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--rooms-root', "`"$roomsRoot`"") `
        -WindowStyle Hidden -PassThru -RedirectStandardError "$scratch\hub.err.log" -RedirectStandardOutput "$scratch\hub.out.log"
    $health = $null
    foreach ($i in 1..60) { try { $health = Invoke-RestMethod "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 } }
    Add-Check 'hub.health-schema-latest' ($health.schema -eq $latestSchema) "schema=$($health.schema) expected=$latestSchema"

    $null = Invoke-RestMethod -Uri "$base/api/rooms/general/messages" -Method Post -Headers $ownerAuth -ContentType 'application/json' `
        -Body (@{ body = '@sonnet reply in the room with the single word ok.' } | ConvertTo-Json)
    $state = $null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { $state = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange" -TimeoutSec 10 } catch { }
        if ($state -and $state.status -in 'concluded', 'stopped') { break }
        Start-Sleep -Seconds 3
    }
    Add-Check 'exchange.concluded' ($state.status -eq 'concluded') "status=$($state.status)"

    $tasks = @(Invoke-RestMethod "$base/api/rooms/general/tasks" -TimeoutSec 10)
    Add-Check 'route.one-task' ($tasks.Count -eq 1) "count=$($tasks.Count)"
    $logged = @(Select-String -LiteralPath "$scratch\hub.err.log" -Pattern '^spawn (\S+): sonnet starting' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    if ($tasks.Count -eq 1) {
        $t = $tasks[0]
        Add-Check 'task.spawn-id-matches-log' ($logged.Count -eq 1 -and $t.spawnId -eq $logged[0]) "task=$($t.spawnId) log=$($logged -join ',')"
        Add-Check 'task.requested-sonnet' ($t.requestedModel -eq 'sonnet' -and $t.host -eq 'claude' -and $t.participantId -eq 'sonnet') "model=$($t.requestedModel) host=$($t.host)"
        Add-Check 'task.no-retry-no-effort' ($t.retry -eq $false -and $null -eq $t.effort) "retry=$($t.retry) effort=$($t.effort)"
        Add-Check 'task.completed' ($t.termination -eq 'completed' -and $t.exitCode -eq 0) "termination=$($t.termination) exit=$($t.exitCode)"
        Add-Check 'task.positive-duration' ($t.durationMs -gt 0) "durationMs=$($t.durationMs)"
        Add-Check 'task.one-post' ($t.posts -eq 1) "posts=$($t.posts)"
        Add-Check 'task.token-numbers' ($t.inputTokens -ge 0 -and $null -ne $t.inputTokens -and $t.outputTokens -gt 0) "input=$($t.inputTokens) output=$($t.outputTokens) cacheRead=$($t.cacheReadTokens) cacheWrite=$($t.cacheWriteTokens)"
        $models = @($t.reportedModels)
        Add-Check 'task.reported-sonnet' (@($models | Where-Object { $_ -like '*sonnet*' }).Count -ge 1) "models=$($models -join ',')"
        Add-Check 'task.tool-calls-counted' ($null -ne $t.toolCalls -and $t.toolCalls -ge 1) "toolCalls=$($t.toolCalls)"
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

Write-Host "Results: $($checks - $fails)/$checks PASS"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
