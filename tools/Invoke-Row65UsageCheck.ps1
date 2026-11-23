#Requires -Version 7
<#
.SYNOPSIS
    Spawn usage live check (real Sonnet, one call, spends): one real Claude spawn through a scratch hub
    stores the usage block it reported on the message it posted, linked to its spawn.

.DESCRIPTION
    Starts the given hub on a fresh scratch data folder under %TEMP% with the real claude CLI on PATH,
    posts one owner message to @sonnet in the plain general room (the `--output-format json` shape),
    waits for the exchange to conclude, and stops the hub. It then reads the scratch database:
      - the reply row's spawn_id equals the id on the hub's "spawn <id>: sonnet starting" stderr line
      - spawn_usage is a JSON object with host "claude", a usage object with output_tokens, and a
        modelUsage object with one key naming a Sonnet model (reported-model attribution only)
      - the block holds no other result field (session_id, uuid, credential_expires_at, result)
      - the owner's own message has neither column set

    No real data folder or rooms folder is read or written. The last line is "Results: n/m PASS".
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$SqliteBin = '',
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$latestSchema = & (Join-Path $PSScriptRoot 'Get-ChopSchemaVersion.ps1')
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$HubExe = (Resolve-Path -LiteralPath $HubExe).Path
$hubBin = if ($SqliteBin) { (Resolve-Path -LiteralPath $SqliteBin).Path } else { Split-Path -Parent $HubExe }
$env:PATH = (Join-Path $hubBin 'runtimes\win-x64\native') + ';' + $env:PATH
foreach ($dll in 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll') {
    Add-Type -Path (Join-Path $hubBin $dll)
}
[SQLitePCL.Batteries_V2]::Init()

$scratch = Join-Path $env:TEMP ('chopitup_usagecheck_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"; $dbPath = "$dataDir\chopitup.db"
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

function Invoke-Sql([string]$Sql) {
    $c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbPath;Mode=ReadOnly;Pooling=False")
    $c.Open()
    try { $cmd = $c.CreateCommand(); $cmd.CommandText = $Sql; $cmd.ExecuteScalar() }
    finally { $c.Dispose() }
}

try {
    Add-Check 'cli.claude-on-path' ([bool](Get-Command claude -ErrorAction SilentlyContinue))
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--rooms-root', "`"$roomsRoot`"") `
        -WindowStyle Hidden -PassThru -RedirectStandardError "$scratch\hub.err.log" -RedirectStandardOutput "$scratch\hub.out.log"
    $health = $null
    foreach ($i in 1..60) { try { $health = Invoke-RestMethod "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 } }
    Add-Check 'hub.health-schema-latest' ($health.schema -eq $latestSchema) "schema=$($health.schema) expected=$latestSchema"

    $posted = Invoke-RestMethod -Uri "$base/api/rooms/general/messages" -Method Post -Headers $ownerAuth -ContentType 'application/json' `
        -Body (@{ body = '@sonnet reply in the room with the single word ok.' } | ConvertTo-Json)
    $state = $null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { $state = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange" -TimeoutSec 10 } catch { }
        if ($state -and $state.status -in 'concluded', 'stopped') { break }
        Start-Sleep -Seconds 3
    }
    Add-Check 'exchange.concluded' ($state.status -eq 'concluded') "status=$($state.status)"
    $reply = @((Invoke-RestMethod "$base/api/rooms/general/messages?afterId=0&limit=200" -TimeoutSec 10).messages | Where-Object { $_.authorId -eq 'sonnet' }) | Select-Object -Last 1
    Add-Check 'spawn.sonnet-posted' ($null -ne $reply)
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    if ($hub -and -not $hub.HasExited) { $hub.Kill($true); $hub.WaitForExit(10000) | Out-Null }
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
}

try {
    if ($reply) {
        $logged = @(Select-String -LiteralPath "$scratch\hub.err.log" -Pattern '^spawn (\S+): sonnet starting' | ForEach-Object { $_.Matches[0].Groups[1].Value })
        Add-Check 'log.one-sonnet-spawn' ($logged.Count -eq 1) "count=$($logged.Count)"
        $spawnId = Invoke-Sql "SELECT spawn_id FROM messages WHERE id = $($reply.id)"
        Add-Check 'row.spawn-id-matches-log' ($logged.Count -eq 1 -and $spawnId -eq $logged[0])
        $usage = Invoke-Sql "SELECT spawn_usage FROM messages WHERE id = $($reply.id)"
        Add-Check 'row.usage-present' ($usage -is [string] -and $usage.Length -gt 0) "bytes=$(if ($usage -is [string]) { $usage.Length } else { 0 })"
        if ($usage -is [string]) {
            $block = $usage | ConvertFrom-Json -AsHashtable
            Add-Check 'block.host-claude' ($block.host -eq 'claude')
            Add-Check 'block.usage-output-tokens' ($block.usage -is [hashtable] -and $block.usage.ContainsKey('output_tokens')) "output_tokens=$($block.usage.output_tokens)"
            $models = @($block.modelUsage.Keys)
            Add-Check 'block.model-reported-sonnet' ($models.Count -ge 1 -and @($models | Where-Object { $_ -like '*sonnet*' }).Count -ge 1) "models=$($models -join ',')"
            Add-Check 'block.only-usage-fields' ((@($block.Keys | Where-Object { $_ -notin 'host', 'usage', 'modelUsage' })).Count -eq 0) "keys=$($block.Keys -join ',')"
        }
        Add-Check 'row.owner-message-unstamped' ([long](Invoke-Sql "SELECT COUNT(*) FROM messages WHERE id = $($posted.id) AND spawn_id IS NULL AND spawn_usage IS NULL") -eq 1)
    }
}
catch { Add-Check 'read' $false $_.Exception.Message }
finally {
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Results: $($checks - $fails)/$checks PASS"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
