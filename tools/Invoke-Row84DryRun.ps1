#Requires -Version 7
<#
.SYNOPSIS
    Dry run of the v16 to v17 migration through the real hub exe, on fabricated data only.

.DESCRIPTION
    Starts the given hub once on a fresh scratch data folder under %TEMP%, stops it, and turns that
    database back into a v16 one: v17 only adds four runs columns, so dropping them and stamping 16 is
    the exact v16 shape. The fixture then gets fabricated messages and one parked cap-spent run that
    used 80 spawns. The hub starts again on it and every check below reads the result: the backup,
    the new columns, the old caps kept as that run's budget, and the run API showing them.

    No real data folder or rooms folder is read or written: --data, --rooms-root and the port are all
    scratch. Needs no model CLI: nothing is posted that could spawn one.

    Ends with `Row 84 dry run: n FAIL`. Exit 0 when every check passes, 1 otherwise.
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

$scratch = Join-Path $env:TEMP ('chopitup_row84dry_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"; $dbPath = "$dataDir\chopitup.db"
New-Item -ItemType Directory -Force $roomsRoot | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$null = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
$fails = 0
$first = $null; $second = $null

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
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

$budgetColumns = "SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name IN ('budget_hours','spawn_budget','waiting_since','waited_seconds')"

try {
    # 1. A fresh database from this exe, then the v16 state it would have had.
    $first = Start-Hub
    Add-Check 'fresh.schema-18' ($first.Health.schema -eq 18) "schema=$($first.Health.schema)"
    Stop-Hub $first.Process
    Add-Check 'fresh.budget-columns' ([long](Invoke-Sql $dbPath $budgetColumns -Scalar -ReadOnly) -eq 4)
    Invoke-Sql $dbPath @"
ALTER TABLE runs DROP COLUMN budget_hours;
ALTER TABLE runs DROP COLUMN spawn_budget;
ALTER TABLE runs DROP COLUMN waiting_since;
ALTER TABLE runs DROP COLUMN waited_seconds;
INSERT INTO messages (room_id, author_id, body, created_at) VALUES
    ('general', 'owner', 'fabricated one', '2026-11-19T10:00:00.000+00:00'),
    ('general', 'opus', 'fabricated two', '2026-11-19T10:01:00.000+00:00');
INSERT INTO runs (room_id, conductor_id, skill_name, arguments, status, reason, cap_spent, phase,
                  root_message_id, started_at, parked_at, parked_seconds, spawns_used, exchanges)
VALUES ('general', 'opus', 'goal', '', 'parked', 'the run used its 80 spawns', 1, 'build/2',
        1, '2026-11-19T08:00:00.0000000+00:00', '2026-11-19T10:00:00.0000000+00:00', 0, 80, 14);
PRAGMA user_version = 16;
"@
    $beforeMessages = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar)
    Add-Check 'fixture.v16' ([long](Invoke-Sql $dbPath 'PRAGMA user_version' -Scalar) -eq 16 -and [long](Invoke-Sql $dbPath $budgetColumns -Scalar) -eq 0) "messages=$beforeMessages"

    # 2. The migration, through the real start path.
    $second = Start-Hub
    Add-Check 'migrated.health-schema-18' ($second.Health.schema -eq 18) "schema=$($second.Health.schema)"

    $baks = @(Get-ChildItem -LiteralPath $dataDir -Filter 'chopitup.db.v16.*.bak')
    Add-Check 'backup.one-v16-file' ($baks.Count -eq 1) "count=$($baks.Count)"
    if ($baks.Count -eq 1) {
        $bak = $baks[0].FullName
        Add-Check 'backup.quick-check-ok' ((Invoke-Sql $bak 'PRAGMA quick_check' -Scalar -ReadOnly) -eq 'ok')
        Add-Check 'backup.stamped-16' ([long](Invoke-Sql $bak 'PRAGMA user_version' -Scalar -ReadOnly) -eq 16)
        Add-Check 'backup.messages-equal' ([long](Invoke-Sql $bak 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)
        Add-Check 'backup.run-without-budget' ([long](Invoke-Sql $bak 'SELECT COUNT(*) FROM runs' -Scalar -ReadOnly) -eq 1 -and [long](Invoke-Sql $bak $budgetColumns -Scalar -ReadOnly) -eq 0)
    }

    Add-Check 'migrated.budget-columns' ([long](Invoke-Sql $dbPath $budgetColumns -Scalar -ReadOnly) -eq 4)
    $row = Invoke-Sql $dbPath "SELECT status || '|' || cap_spent || '|' || spawns_used || '|' || budget_hours || '|' || spawn_budget || '|' || waited_seconds || '|' || IFNULL(waiting_since, '-') FROM runs" -Scalar -ReadOnly
    Add-Check 'migrated.run-keeps-old-caps' ($row -eq 'parked|1|80|8|80|0|-') "row=$row"
    Add-Check 'migrated.messages-equal' ([long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)

    $run = Invoke-RestMethod "$base/api/rooms/general/run" -TimeoutSec 10
    Add-Check 'api.run-caps' ($run.spawnCap -eq 80 -and $run.wallClockCapMinutes -eq 480 -and $run.status -eq 'parked') "spawnCap=$($run.spawnCap) wallClockCapMinutes=$($run.wallClockCapMinutes) status=$($run.status)"
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    if ($second) { Stop-Hub $second.Process } elseif ($first) { Stop-Hub $first.Process }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Row 84 dry run: $fails FAIL"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
