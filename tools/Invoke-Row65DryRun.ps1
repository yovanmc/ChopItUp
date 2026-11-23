#Requires -Version 7
<#
.SYNOPSIS
    Dry run of the v19 to v20 migration (the messages spawn columns) through the real hub exe, on
    fabricated data only.

.DESCRIPTION
    Starts the given hub on a fresh scratch data folder under %TEMP%, stops it, and turns that database
    back into a v19 one: v20 only adds messages.spawn_id and messages.spawn_usage, and v21 only the
    spawn_tasks table, so dropping those two columns and that table
    and stamping 19 is the exact v19 shape. Fabricated messages go in. The hub starts again on
    it: the backup, the stamp, both columns and the untouched old rows are checked. A third start must
    take no second backup.

    No real data folder or rooms folder is read or written: --data, --rooms-root and the port are all
    scratch. Needs no model CLI.

    The last line is "Results: n/m PASS". Exit 0 only when every check passes.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    # The folder the SQLite assemblies load from. Defaults to the exe's own folder; an installed
    # single-file exe has none, so point this at a build output.
    [string]$SqliteBin = ''
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

$scratch = Join-Path $env:TEMP ('chopitup_spawnusagedry_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"; $dbPath = "$dataDir\chopitup.db"
New-Item -ItemType Directory -Force $roomsRoot | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$null = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
$fails = 0
$checks = 0
$first = $null; $second = $null; $third = $null
$columns = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name IN ('spawn_id','spawn_usage')"

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks++
    if (-not $Passed) { $script:fails++ }
    Write-Host ("{0,-5} {1}  {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail).TrimEnd()
}

function Start-Hub {
    $p = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--rooms-root', "`"$roomsRoot`"") `
        -WindowStyle Hidden -PassThru -RedirectStandardError "$scratch\hub.err.log" -RedirectStandardOutput "$scratch\hub.out.log"
    foreach ($i in 1..60) {
        if ($p.HasExited) { throw "hub exited early (code $($p.ExitCode))" }
        try { return @{ Process = $p; Health = Invoke-RestMethod "$base/health" -TimeoutSec 2 } } catch { Start-Sleep -Milliseconds 500 }
    }
    Stop-Hub $p
    throw 'scratch hub never answered /health'
}

function Stop-Hub($p) {
    if ($p -and -not $p.HasExited) { $p.Kill($true); $p.WaitForExit(10000) | Out-Null }
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
}

function Invoke-Sql([string]$Path, [string]$Sql, [switch]$Scalar, [switch]$ReadOnly) {
    $mode = if ($ReadOnly) { 'ReadOnly' } else { 'ReadWrite' }
    $c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$Path;Mode=$mode;Pooling=False")
    $c.Open()
    try { $cmd = $c.CreateCommand(); $cmd.CommandText = $Sql; if ($Scalar) { $cmd.ExecuteScalar() } else { [void]$cmd.ExecuteNonQuery() } }
    finally { $c.Dispose() }
}

try {
    # 1. A fresh database from this exe, then the v19 state it would have had, with fabricated rows.
    $first = Start-Hub
    Add-Check 'fresh.schema-latest' ($first.Health.schema -eq $latestSchema) "schema=$($first.Health.schema) expected=$latestSchema"
    Stop-Hub $first.Process
    Add-Check 'fresh.spawn-columns' ([long](Invoke-Sql $dbPath $columns -Scalar -ReadOnly) -eq 2)
    Invoke-Sql $dbPath @"
ALTER TABLE messages DROP COLUMN spawn_id;
ALTER TABLE messages DROP COLUMN spawn_usage;
DROP INDEX ix_spawn_tasks_room;
DROP TABLE spawn_tasks;
INSERT INTO messages (room_id, author_id, body, created_at, client_key) VALUES
    ('general', 'owner', 'fabricated question @opus', '2026-11-22T10:00:00.000+00:00', NULL),
    ('general', 'opus', 'fabricated answer', '2026-11-22T10:01:00.000+00:00', 'general-1-1-0000abcd'),
    ('general', 'gpt-6-astra', 'fabricated second answer', '2026-11-22T10:02:00.000+00:00', NULL);
PRAGMA user_version = 19;
"@
    $beforeMessages = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar)
    Add-Check 'fixture.v19' ([long](Invoke-Sql $dbPath 'PRAGMA user_version' -Scalar) -eq 19 -and [long](Invoke-Sql $dbPath $columns -Scalar) -eq 0) "messages=$beforeMessages"

    # 2. The migration, through the real start path.
    $second = Start-Hub
    Add-Check 'migrated.health-schema-latest' ($second.Health.schema -eq $latestSchema) "schema=$($second.Health.schema) expected=$latestSchema"
    Stop-Hub $second.Process
    $baks = @(Get-ChildItem -LiteralPath $dataDir -Filter 'chopitup.db.v19.*.bak')
    Add-Check 'backup.one-v19-file' ($baks.Count -eq 1) "count=$($baks.Count)"
    if ($baks.Count -eq 1) {
        $bak = $baks[0].FullName
        Add-Check 'backup.quick-check-ok' ((Invoke-Sql $bak 'PRAGMA quick_check' -Scalar -ReadOnly) -eq 'ok')
        Add-Check 'backup.stamped-19' ([long](Invoke-Sql $bak 'PRAGMA user_version' -Scalar -ReadOnly) -eq 19)
        Add-Check 'backup.messages-equal' ([long](Invoke-Sql $bak 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)
        Add-Check 'backup.no-spawn-columns' ([long](Invoke-Sql $bak $columns -Scalar -ReadOnly) -eq 0)
    }
    Add-Check 'migrated.spawn-columns' ([long](Invoke-Sql $dbPath $columns -Scalar -ReadOnly) -eq 2)
    Add-Check 'migrated.messages-equal' ([long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)
    Add-Check 'migrated.old-rows-not-reported' ([long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages WHERE spawn_id IS NOT NULL OR spawn_usage IS NOT NULL' -Scalar -ReadOnly) -eq 0)
    Add-Check 'migrated.bodies-intact' ([long](Invoke-Sql $dbPath "SELECT COUNT(*) FROM messages WHERE body LIKE 'fabricated%'" -Scalar -ReadOnly) -eq 3)

    # 3. A restart on the migrated store takes no second backup and keeps the stamp.
    $third = Start-Hub
    Add-Check 'restart.health-schema-latest' ($third.Health.schema -eq $latestSchema) "schema=$($third.Health.schema) expected=$latestSchema"
    Stop-Hub $third.Process
    Add-Check 'restart.no-second-backup' (@(Get-ChildItem -LiteralPath $dataDir -Filter 'chopitup.db.v*.bak').Count -eq 1)
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    foreach ($h in @($third, $second, $first)) { if ($h) { Stop-Hub $h.Process } }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Results: $($checks - $fails)/$checks PASS"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
