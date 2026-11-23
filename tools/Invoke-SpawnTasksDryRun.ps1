#Requires -Version 7
<#
.SYNOPSIS
    Dry run of the v20 to v21 migration (the spawn_tasks table) through the real hub exe, on
    fabricated data only.

.DESCRIPTION
    Starts the given hub on a fresh scratch data folder under %TEMP%, stops it, and turns that database
    back into a v20 one: v21 only adds the spawn_tasks table and its index, so dropping both and
    stamping 20 is the exact v20 shape. Fabricated messages go in, one of them stamped with a spawn id
    and a usage block. The hub starts again on it: the backup, the stamp, the empty table with its 18
    columns and index, the untouched old rows and an empty tasks route are checked. A third start must
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
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$HubExe = (Resolve-Path -LiteralPath $HubExe).Path
$hubBin = if ($SqliteBin) { (Resolve-Path -LiteralPath $SqliteBin).Path } else { Split-Path -Parent $HubExe }
$env:PATH = (Join-Path $hubBin 'runtimes\win-x64\native') + ';' + $env:PATH
foreach ($dll in 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll') {
    Add-Type -Path (Join-Path $hubBin $dll)
}
[SQLitePCL.Batteries_V2]::Init()

$scratch = Join-Path $env:TEMP ('chopitup_spawntasksdry_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"; $dbPath = "$dataDir\chopitup.db"
New-Item -ItemType Directory -Force $roomsRoot | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$null = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
$fails = 0
$checks = 0
$first = $null; $second = $null; $third = $null
$table = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'spawn_tasks'"
$index = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_spawn_tasks_room'"
$columns = "SELECT COUNT(*) FROM pragma_table_info('spawn_tasks')"
$stamped = "SELECT COUNT(*) FROM messages WHERE spawn_id = 'general-1-1-0000abcd' AND spawn_usage = '{""host"":""claude""}'"

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
    # 1. A fresh database from this exe, then the v20 state it would have had, with fabricated rows.
    $first = Start-Hub
    Add-Check 'fresh.schema-21' ($first.Health.schema -eq 21) "schema=$($first.Health.schema)"
    Stop-Hub $first.Process
    Add-Check 'fresh.table-and-index' ([long](Invoke-Sql $dbPath $table -Scalar -ReadOnly) -eq 1 -and [long](Invoke-Sql $dbPath $index -Scalar -ReadOnly) -eq 1)
    Invoke-Sql $dbPath @"
DROP INDEX ix_spawn_tasks_room;
DROP TABLE spawn_tasks;
INSERT INTO messages (room_id, author_id, body, created_at, client_key, spawn_id, spawn_usage) VALUES
    ('general', 'owner', 'fabricated question @opus', '2026-11-22T10:00:00.000+00:00', NULL, NULL, NULL),
    ('general', 'opus', 'fabricated answer', '2026-11-22T10:01:00.000+00:00', 'general-1-1-0000abcd', 'general-1-1-0000abcd', '{"host":"claude"}'),
    ('general', 'gpt-6-astra', 'fabricated second answer', '2026-11-22T10:02:00.000+00:00', NULL, NULL, NULL);
PRAGMA user_version = 20;
"@
    $beforeMessages = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar)
    Add-Check 'fixture.v20' ([long](Invoke-Sql $dbPath 'PRAGMA user_version' -Scalar) -eq 20 -and [long](Invoke-Sql $dbPath $table -Scalar) -eq 0) "messages=$beforeMessages"

    # 2. The migration, through the real start path.
    $second = Start-Hub
    Add-Check 'migrated.health-schema-21' ($second.Health.schema -eq 21) "schema=$($second.Health.schema)"
    $tasks = Invoke-WebRequest "$base/api/rooms/general/tasks" -TimeoutSec 5
    Add-Check 'migrated.tasks-route-empty' ($tasks.StatusCode -eq 200 -and @($tasks.Content | ConvertFrom-Json).Count -eq 0) "status=$($tasks.StatusCode) body=$($tasks.Content)"
    Stop-Hub $second.Process
    $baks = @(Get-ChildItem -LiteralPath $dataDir -Filter 'chopitup.db.v20.*.bak')
    Add-Check 'backup.one-v20-file' ($baks.Count -eq 1) "count=$($baks.Count)"
    if ($baks.Count -eq 1) {
        $bak = $baks[0].FullName
        Add-Check 'backup.quick-check-ok' ((Invoke-Sql $bak 'PRAGMA quick_check' -Scalar -ReadOnly) -eq 'ok')
        Add-Check 'backup.stamped-20' ([long](Invoke-Sql $bak 'PRAGMA user_version' -Scalar -ReadOnly) -eq 20)
        Add-Check 'backup.messages-equal' ([long](Invoke-Sql $bak 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)
        Add-Check 'backup.no-spawn-tasks' ([long](Invoke-Sql $bak $table -Scalar -ReadOnly) -eq 0)
    }
    Add-Check 'migrated.table-18-columns' ([long](Invoke-Sql $dbPath $columns -Scalar -ReadOnly) -eq 18)
    Add-Check 'migrated.index' ([long](Invoke-Sql $dbPath $index -Scalar -ReadOnly) -eq 1)
    Add-Check 'migrated.table-empty' ([long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM spawn_tasks' -Scalar -ReadOnly) -eq 0)
    Add-Check 'migrated.messages-equal' ([long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)
    Add-Check 'migrated.stamp-and-usage-intact' ([long](Invoke-Sql $dbPath $stamped -Scalar -ReadOnly) -eq 1)
    Add-Check 'migrated.bodies-intact' ([long](Invoke-Sql $dbPath "SELECT COUNT(*) FROM messages WHERE body LIKE 'fabricated%'" -Scalar -ReadOnly) -eq 3)

    # 3. A restart on the migrated store takes no second backup and keeps the stamp.
    $third = Start-Hub
    Add-Check 'restart.health-schema-21' ($third.Health.schema -eq 21) "schema=$($third.Health.schema)"
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
