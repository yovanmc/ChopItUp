#Requires -Version 7
<#
.SYNOPSIS
    Dry run of the v17 to v18 migration and of lost-job recovery through the real hub exe, on
    fabricated data only.

.DESCRIPTION
    Starts the given hub on a fresh scratch data folder under %TEMP%, stops it, and turns that database
    back into a v17 one: v18 only adds the jobs table and its index, so dropping both and stamping 17
    is the exact v17 shape. The hub starts again on it: the backup, the table and the messages are
    checked. Then a fabricated job row is left 'running', as a hub that crashed mid-job leaves it,
    with a parked run it belonged to and a cancel command that writes a marker file in the scratch
    folder. The third start must end the row as lost, run the cancel command once, post the lost note
    and mark it noted.

    No real data folder or rooms folder is read or written: --data, --rooms-root and the port are all
    scratch. The fabricated job is not a GPU job, so the machine's GPU lock file is never touched.
    Needs pwsh on PATH (the hub runs the cancel command with it) and no model CLI.

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

$scratch = Join-Path $env:TEMP ('chopitup_jobsdry_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"; $dbPath = "$dataDir\chopitup.db"
New-Item -ItemType Directory -Force $roomsRoot | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$null = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
$fails = 0
$checks = 0
$first = $null; $second = $null; $third = $null

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
    # 1. A fresh database from this exe, then the v17 state it would have had.
    $first = Start-Hub
    Add-Check 'fresh.schema-21' ($first.Health.schema -eq 21) "schema=$($first.Health.schema)"
    Stop-Hub $first.Process
    Add-Check 'fresh.jobs-table' ([long](Invoke-Sql $dbPath "SELECT COUNT(*) FROM pragma_table_info('jobs')" -Scalar -ReadOnly) -eq 16)
    Invoke-Sql $dbPath @"
DROP INDEX ix_jobs_status;
DROP TABLE jobs;
INSERT INTO messages (room_id, author_id, body, created_at) VALUES
    ('general', 'owner', 'fabricated one', '2026-11-20T10:00:00.000+00:00'),
    ('general', 'opus', 'fabricated two', '2026-11-20T10:01:00.000+00:00');
PRAGMA user_version = 17;
"@
    $beforeMessages = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar)
    Add-Check 'fixture.v17' ([long](Invoke-Sql $dbPath 'PRAGMA user_version' -Scalar) -eq 17 -and [long](Invoke-Sql $dbPath "SELECT COUNT(*) FROM sqlite_master WHERE name = 'jobs'" -Scalar) -eq 0) "messages=$beforeMessages"

    # 2. The migration, through the real start path.
    $second = Start-Hub
    Add-Check 'migrated.health-schema-21' ($second.Health.schema -eq 21) "schema=$($second.Health.schema)"
    Stop-Hub $second.Process
    $baks = @(Get-ChildItem -LiteralPath $dataDir -Filter 'chopitup.db.v17.*.bak')
    Add-Check 'backup.one-v17-file' ($baks.Count -eq 1) "count=$($baks.Count)"
    if ($baks.Count -eq 1) {
        $bak = $baks[0].FullName
        Add-Check 'backup.quick-check-ok' ((Invoke-Sql $bak 'PRAGMA quick_check' -Scalar -ReadOnly) -eq 'ok')
        Add-Check 'backup.stamped-17' ([long](Invoke-Sql $bak 'PRAGMA user_version' -Scalar -ReadOnly) -eq 17)
        Add-Check 'backup.messages-equal' ([long](Invoke-Sql $bak 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)
        Add-Check 'backup.no-jobs-table' ([long](Invoke-Sql $bak "SELECT COUNT(*) FROM sqlite_master WHERE name = 'jobs'" -Scalar -ReadOnly) -eq 0)
    }
    Add-Check 'migrated.jobs-table-empty' ([long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM jobs' -Scalar -ReadOnly) -eq 0)
    Add-Check 'migrated.messages-equal' ([long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)

    # 3. A job a crashed hub left running, with the parked run it belonged to.
    $jobFolder = "$scratch\artifacts\jobs\j1-dryrun"
    New-Item -ItemType Directory -Force $jobFolder | Out-Null
    Set-Content -LiteralPath "$jobFolder\job.log" -Value "frame 1`nframe 2"
    $marker = "$scratch\cancel-ran.txt"
    $cancel = "Add-Content -LiteralPath '$marker' 'x'"
    Invoke-Sql $dbPath @"
INSERT INTO runs (room_id, conductor_id, skill_name, arguments, status, reason, cap_spent, phase,
                  root_message_id, started_at, parked_at, parked_seconds, spawns_used, exchanges)
VALUES ('general', 'opus', 'goal', '', 'parked', 'the hub restarted while this run was active', 0, 'build/1',
        1, '2026-11-20T08:00:00.0000000+00:00', '2026-11-20T10:00:00.0000000+00:00', 0, 3, 1);
INSERT INTO jobs (room_id, run_id, author_id, command, cancel_command, gpu, timeout_minutes, folder, status, pid, process_started_at, started_at)
VALUES ('general', (SELECT MAX(id) FROM runs), 'sonnet', 'render', '$($cancel.Replace("'", "''"))', 0, 120, '$jobFolder', 'running', 999999,
        '2026-11-20T09:00:01.0000000+00:00', '2026-11-20T09:00:00.0000000+00:00');
"@

    # 4. The restart reports it.
    $third = Start-Hub
    $row = $null
    foreach ($i in 1..60) {
        $row = Invoke-Sql $dbPath "SELECT status || '|' || IFNULL(noted_at, '-') FROM jobs WHERE id = 1" -Scalar -ReadOnly
        if ($row -like 'lost|2*') { break }
        Start-Sleep -Milliseconds 250
    }
    Add-Check 'recovered.row-lost-and-noted' ($row -like 'lost|2*') "row=$row"
    Add-Check 'recovered.cancel-ran-once' ((Test-Path -LiteralPath $marker) -and @(Get-Content -LiteralPath $marker).Count -eq 1)
    $messages = (Invoke-RestMethod "$base/api/rooms/general/messages?afterId=0&limit=200" -TimeoutSec 10).messages
    $note = @($messages | Where-Object { $_.authorId -eq 'hub' -and $_.body -like 'Job j1 by @sonnet was lost: the hub stopped while it ran.*' })
    Add-Check 'recovered.lost-note' ($note.Count -eq 1 -and $note[0].body -like '*frame 2*') "notes=$($note.Count)"
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
