#Requires -Version 7
<#
.SYNOPSIS
    Dry run of the v15 to v16 migration through the real hub exe, on fabricated data only.

.DESCRIPTION
    Starts the given hub once on a fresh scratch data folder under %TEMP%, stops it, and turns that
    database back into a v15 one: v16 changes rows only, so deleting opus-judge, clearing the Sol and
    Astra classes, restoring the old owner-remote note and stamping 15 is the exact v15 state. The
    fixture then gets fabricated messages, an owner-set class on Astra (judge) and an owner clear on
    Sonnet. The hub starts again on it and every check below reads the result.

    No real data folder or rooms folder is read or written: --data, --rooms-root and the port are all
    scratch. Needs no model CLI: nothing is posted that could spawn one.

    Exit 0 when every check passes, 1 otherwise.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$HubExe = (Resolve-Path -LiteralPath $HubExe).Path
$hubBin = Split-Path -Parent $HubExe
$env:PATH = (Join-Path $hubBin 'runtimes\win-x64\native') + ';' + $env:PATH
foreach ($dll in 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll') {
    Add-Type -Path (Join-Path $hubBin $dll)
}
[SQLitePCL.Batteries_V2]::Init()

$scratch = Join-Path $env:TEMP ('chopitup_row82dry_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"; $dbPath = "$dataDir\chopitup.db"
New-Item -ItemType Directory -Force $roomsRoot | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$tokens = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
$owner = New-ChopBearerHeaders -Token $tokens.owner
$fails = 0
$hub = $null

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

$oldNote = 'The owner, posting from a session on another device. Same authority as owner; the hub stamps which hand typed. Last in the roster because rowid order is seed order and this row is newer than every other.'
$newNote = 'The owner, posting from a session on another device. Same authority as owner; the hub stamps which hand typed.'

try {
    # 1. A fresh database from this exe, then the v15 state it would have had.
    $first = Start-Hub
    Add-Check 'fresh.schema-16' ($first.Health.schema -eq 16) "schema=$($first.Health.schema)"
    Stop-Hub $first.Process
    Invoke-Sql $dbPath @"
DELETE FROM participants WHERE id = 'opus-judge';
UPDATE participants SET classes = NULL WHERE id = 'gpt-5.6-sol';
UPDATE participants SET classes = 'judge' WHERE id = 'gpt-6-astra';
UPDATE participants SET classes = NULL WHERE id = 'sonnet';
UPDATE participants SET note = '$($oldNote -replace "'", "''")' WHERE id = 'owner-remote';
INSERT INTO messages (room_id, author_id, body, created_at) VALUES
    ('general', 'owner', 'fabricated one', '2026-11-19T10:00:00.000+00:00'),
    ('general', 'opus', 'fabricated two', '2026-11-19T10:01:00.000+00:00'),
    ('general', 'owner', 'fabricated three', '2026-11-19T10:02:00.000+00:00');
PRAGMA user_version = 15;
"@
    $beforeMessages = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar)
    $beforeRows = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM participants' -Scalar)
    Add-Check 'fixture.v15' ([long](Invoke-Sql $dbPath 'PRAGMA user_version' -Scalar) -eq 15) "messages=$beforeMessages participants=$beforeRows"

    # 2. The migration, through the real start path.
    $second = Start-Hub
    Add-Check 'migrated.health-schema-16' ($second.Health.schema -eq 16) "schema=$($second.Health.schema)"

    $baks = @(Get-ChildItem -LiteralPath $dataDir -Filter 'chopitup.db.v15.*.bak')
    Add-Check 'backup.one-v15-file' ($baks.Count -eq 1) "count=$($baks.Count)"
    if ($baks.Count -eq 1) {
        $bak = $baks[0].FullName
        Add-Check 'backup.quick-check-ok' ((Invoke-Sql $bak 'PRAGMA quick_check' -Scalar -ReadOnly) -eq 'ok')
        Add-Check 'backup.stamped-15' ([long](Invoke-Sql $bak 'PRAGMA user_version' -Scalar -ReadOnly) -eq 15)
        Add-Check 'backup.messages-equal' ([long](Invoke-Sql $bak 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly) -eq $beforeMessages)
        Add-Check 'backup.no-opus-judge' ([long](Invoke-Sql $bak "SELECT COUNT(*) FROM participants WHERE id = 'opus-judge'" -Scalar -ReadOnly) -eq 0)
    }

    $row = Invoke-Sql $dbPath "SELECT host || '|' || model || '|' || classes FROM participants WHERE id = 'opus-judge'" -Scalar -ReadOnly
    Add-Check 'migrated.opus-judge' ($row -eq 'claude|opus|judge') "row=$row"
    Add-Check 'migrated.opus-judge-last' ((Invoke-Sql $dbPath 'SELECT id FROM participants ORDER BY rowid DESC LIMIT 1' -Scalar -ReadOnly) -eq 'opus-judge')
    $sol = Invoke-Sql $dbPath "SELECT classes FROM participants WHERE id = 'gpt-5.6-sol'" -Scalar -ReadOnly
    Add-Check 'migrated.sol-judge' ($sol -eq 'judge') "classes=$sol"
    $astra = Invoke-Sql $dbPath "SELECT classes FROM participants WHERE id = 'gpt-6-astra'" -Scalar -ReadOnly
    Add-Check 'migrated.astra-owner-class-kept' ($astra -eq 'judge') "classes=$astra"
    $sonnet = Invoke-Sql $dbPath "SELECT classes FROM participants WHERE id = 'sonnet'" -Scalar -ReadOnly
    Add-Check 'migrated.sonnet-owner-clear-kept' ($sonnet -is [DBNull]) "classes=$sonnet"
    $note = Invoke-Sql $dbPath "SELECT note FROM participants WHERE id = 'owner-remote'" -Scalar -ReadOnly
    Add-Check 'migrated.owner-remote-note' ($note -eq $newNote)
    $afterMessages = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM messages' -Scalar -ReadOnly)
    Add-Check 'migrated.messages-equal' ($afterMessages -eq $beforeMessages) "before=$beforeMessages after=$afterMessages"
    $afterRows = [long](Invoke-Sql $dbPath 'SELECT COUNT(*) FROM participants' -Scalar -ReadOnly)
    Add-Check 'migrated.one-row-added' ($afterRows -eq $beforeRows + 1) "before=$beforeRows after=$afterRows"

    $listed = @((Invoke-RestMethod "$base/api/participants" -Headers $owner -TimeoutSec 10) | ForEach-Object { $_ })
    $judge = @($listed | Where-Object { $_.id -eq 'opus-judge' })
    Add-Check 'api.lists-opus-judge' ($judge.Count -eq 1 -and $judge[0].displayName -eq 'Opus Judge') "rows=$($listed.Count) displayName=$($judge.displayName)"
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    if ($second) { Stop-Hub $second.Process } elseif ($first) { Stop-Hub $first.Process }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Row 82 dry run: $fails FAIL"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
