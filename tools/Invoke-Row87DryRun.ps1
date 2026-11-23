#Requires -Version 7
<#
.SYNOPSIS
    Dry run of the v18 to v19 migration and of a usage-limit hold resuming across a restart, through
    the real hub exe, on fabricated data only.

.DESCRIPTION
    Starts the given hub on a fresh scratch data folder under %TEMP%, stops it, and turns that database
    back into a v18 one: v19 only adds runs.resume_at, so dropping that column and stamping 18 is the
    exact v18 shape. The hub starts again on it: the backup, the column and the messages are checked.
    Then a fabricated run is left parked with a resume instant already past, as a hub stopped during
    a usage-limit hold leaves it. It has spent more spawns than its budget, so the resume decides the
    cap-spent refusal and launches no model CLI. The third start must clear the instant, post the
    reset note and the refusal, and leave the run parked.

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

$scratch = Join-Path $env:TEMP ('chopitup_limitsdry_' + [guid]::NewGuid().ToString('N'))
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
    # 1. A fresh database from this exe, then the v18 state it would have had.
    $first = Start-Hub
    Add-Check 'fresh.schema-21' ($first.Health.schema -eq 21) "schema=$($first.Health.schema)"
    Stop-Hub $first.Process
    Add-Check 'fresh.resume-column' ([long](Invoke-Sql $dbPath "SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name = 'resume_at'" -Scalar -ReadOnly) -eq 1)
    Invoke-Sql $dbPath @"
ALTER TABLE runs DROP COLUMN resume_at;
INSERT INTO messages (room_id, author_id, body, created_at) VALUES
    ('general', 'owner', 'fabricated one', '2026-11-19T10:00:00.000+00:00'),
    ('general', 'opus', 'fabricated two', '2026-11-19T10:01:00.000+00:00');
PRAGMA user_version = 18;
"@
    $beforeMessages = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar)
    Add-Check 'fixture.v18' ([long](Invoke-Sql $dbPath 'PRAGMA user_version' -Scalar) -eq 18 -and [long](Invoke-Sql $dbPath "SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name = 'resume_at'" -Scalar) -eq 0) "messages=$beforeMessages"

    # 2. The migration, through the real start path.
    $second = Start-Hub
    Add-Check 'migrated.health-schema-21' ($second.Health.schema -eq 21) "schema=$($second.Health.schema)"
    Stop-Hub $second.Process
    $baks = @(Get-ChildItem -LiteralPath $dataDir -Filter 'chopitup.db.v18.*.bak')
    Add-Check 'backup.one-v18-file' ($baks.Count -eq 1) "count=$($baks.Count)"
    if ($baks.Count -eq 1) {
        $bak = $baks[0].FullName
        Add-Check 'backup.quick-check-ok' ((Invoke-Sql $bak 'PRAGMA quick_check' -Scalar -ReadOnly) -eq 'ok')
        Add-Check 'backup.stamped-18' ([long](Invoke-Sql $bak 'PRAGMA user_version' -Scalar -ReadOnly) -eq 18)
        Add-Check 'backup.messages-equal' ([long](Invoke-Sql $bak 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)
        Add-Check 'backup.no-resume-column' ([long](Invoke-Sql $bak "SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name = 'resume_at'" -Scalar -ReadOnly) -eq 0)
    }
    Add-Check 'migrated.resume-column' ([long](Invoke-Sql $dbPath "SELECT COUNT(*) FROM pragma_table_info('runs') WHERE name = 'resume_at'" -Scalar -ReadOnly) -eq 1)
    Add-Check 'migrated.messages-equal' ([long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)

    # 3. A run a stopped hub left held, its reset already past and its spawns over budget.
    Invoke-Sql $dbPath @"
INSERT INTO runs (room_id, conductor_id, skill_name, arguments, status, reason, cap_spent, phase,
                  root_message_id, started_at, parked_at, parked_seconds, spawns_used, exchanges, resume_at)
VALUES ('general', 'opus', 'goal', '', 'parked', '@opus hit a usage limit; resumes at 2026-11-19 12:02 hub time', 0, 'build/1',
        1, '2026-11-19T08:00:00.0000000+00:00', '2026-11-19T10:00:00.0000000+00:00', 0, 999, 1,
        '2026-11-19T12:02:00.0000000+00:00');
"@
    $runId = [long](Invoke-Sql $dbPath 'SELECT MAX(id) FROM runs' -Scalar -ReadOnly)

    # 4. The restart resumes it at once and the cap decides.
    $third = Start-Hub
    $row = $null
    foreach ($i in 1..60) {
        $row = Invoke-Sql $dbPath "SELECT status || '|' || IFNULL(resume_at, '-') FROM runs WHERE id = $runId" -Scalar -ReadOnly
        if ($row -eq 'parked|-') { break }
        Start-Sleep -Milliseconds 250
    }
    Add-Check 'resumed.instant-cleared-still-parked' ($row -eq 'parked|-') "row=$row"
    $messages = @()
    foreach ($i in 1..40) {
        $messages = @((Invoke-RestMethod "$base/api/rooms/general/messages?afterId=0&limit=200" -TimeoutSec 10).messages | Where-Object { $_.authorId -eq 'hub' })
        if (@($messages | Where-Object { $_.body -like '*a cap is spent*' }).Count -gt 0) { break }
        Start-Sleep -Milliseconds 250
    }
    $reset = @($messages | Where-Object { $_.body -eq "Run #${runId}: the usage-limit reset passed." })
    Add-Check 'resumed.reset-note' ($reset.Count -eq 1) "notes=$($reset.Count)"
    $refusal = @($messages | Where-Object { $_.body -like '*a cap is spent*' })
    Add-Check 'resumed.cap-refusal' ($refusal.Count -eq 1) "notes=$($refusal.Count)"
    Add-Check 'resumed.no-spawn-failure' (-not (Test-Path -LiteralPath "$dataDir\spawn-failures"))
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
