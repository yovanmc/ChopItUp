<#
.SYNOPSIS
    Row 14 Task 7 (issues/07-self-check.md): deploy-day evidence that the whole roles/personas path
    works -- a v11 fixture migrated to v13 by the REAL hub, a persona and a role set and read back over
    the API, the three write routes refused with no credential and unchanged storage, a role edit
    visible in the next GET with no hub restart, and the room_roles foreign key actually enforced.

.DESCRIPTION
    Generated from ~\.claude\skills\roadmap\references\desk-check-template.ps1, adapted the way
    Invoke-Row12ShellCheck.ps1 and Invoke-Row28SelfCheck.ps1 already adapt it for a permanent, committed
    tool rather than a one-off .scratch\desk-checks\ copy: a RunId from the target exe's mtime+size+
    harness version, one evidence directory per run (never reused), an explicit boolean passed to every
    Add-Check call, and a log-line shape restricted to booleans/counts/exit codes -- never message
    content, a token, or a filename under a real data dir.

    THE FIXTURE (ledger 21/22): tools/ChopItUp.Corpus refuses any schema but v1/v2
    (CorpusBuilder.cs:64), so it cannot build the v11 fixture this dry run needs, and a v2 corpus
    replayed through the whole ladder would not model the deployed jump -- the live install is at
    schema 11, so the real migration is the v11->v13 chain. Following Invoke-M25DryRun.ps1's own
    precedent exactly: the real, already-built Microsoft.Data.Sqlite.dll is loaded straight out of the
    hub's own bin output (Add-Type, with runtimes\win-x64\native prepended onto PATH so the native
    provider resolves outside the hub's own AppContext.BaseDirectory), and the v11 fixture is written
    with raw SQL -- the v9 cumulative shape transcribed in Invoke-M25DryRun.ps1, plus v10's
    skill_proposals table (ChopDb.cs ApplyV10) and v11's messages.reply_to_id column (ApplyV11) -- then
    stamped PRAGMA user_version = 11.

    THE HUB is launched directly from its own build output (never `dotnet run`), matching
    Invoke-M25DryRun.ps1's PID-identity reasoning. Every path this script touches is rooted under a
    fresh $env:TEMP scratch directory with a GUID nonce -- never C:\Self Apps, never the repo's own
    .data\, never a real data directory or its tokens.json. The scratch hub's OWN tokens.json is seeded
    before its first start via ChopTokenHelpers.ps1's Initialize-ChopScratchTokens (the same helper
    Invoke-Row28SelfCheck.ps1 and Invoke-Row12ShellCheck.ps1 already use), so the owner bearer used for
    every authenticated leg below is one this script minted itself, never anything read off a real
    installation.

    DATABASE BOUNDARY: the fixture-writing and post-migration-assertion connections below are the
    explicit subject under test (this row IS a schema migration), so they are the deliberate exception
    to the desk-check template's "never open a database" rule -- exactly as Invoke-M25DryRun.ps1 is.
    Every connection here is against the scratch database this script created, never a real one.

    LEGS (mapped to issues/07-self-check.md's acceptance criteria):
      fixture.*      -- build and stamp the v11 database.
      hub.*          -- launch the real exe, wait for /health.
      migrated.*     -- schema 14, every pre-migration table's row count and room name preserved,
                        the two new columns present and NULL, room_roles present and empty.
      api.*          -- a persona and a role set over the API and read back.
      auth.*         -- each of the three write routes refused with no credential, storage unchanged.
      live-edit.*    -- a role changed while the hub keeps running is visible in the next GET, no
                        restart (AC7's own claim, the one a startup-snapshot implementation fails).
      fk.*           -- an override insert naming a non-existent room fails at the database
                        (ChopDb.Open's PRAGMA foreign_keys=ON, ledger 20), not silently.

.PARAMETER KeepEvidence
    Keep the scratch directory (fixture database, hub stdout/stderr) instead of deleting it at the end.
    The evidence LOG itself is always kept, under -EvidenceRoot, regardless of this switch.

.PARAMETER EvidenceRoot
    Where the evidence log and (with -KeepEvidence) the scratch directory contents are copied. Defaults
    under this repo's already-gitignored .scratch\m14-roles-personas\evidence\ (see .gitignore:10).
#>
[CmdletBinding()]
param(
    [switch]$KeepEvidence,
    [string]$EvidenceRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) '.scratch\m14-roles-personas\evidence')
)

$ErrorActionPreference = 'Stop'
$HarnessVersion = 'row14-rolescheck-v1'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hubBin = Join-Path $repoRoot 'src\ChopItUp.Hub\bin\Debug\net10.0'
$hubExe = Join-Path $hubBin 'ChopItUp.Hub.exe'

# ===== evidence directory (template convention: nested .gitignore, refused over tracked files) =======

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$evidenceDir = Join-Path $EvidenceRoot $stamp
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

$trackedHere = @()
try { $trackedHere = @(& git -C $evidenceDir ls-files 2>$null) } catch { }
if ($trackedHere.Count -gt 0) {
    throw "DENIED: '$evidenceDir' holds $($trackedHere.Count) git-tracked file(s); refusing to write '*' over it."
}
Set-Content -LiteralPath (Join-Path $evidenceDir '.gitignore') -Value '*' -Encoding ascii

Get-ChildItem -LiteralPath $EvidenceRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d{8}-\d{6}$' } |
    Sort-Object Name -Descending | Select-Object -Skip 3 |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

$log = Join-Path $evidenceDir 'Invoke-Row14RolesCheck.log'

$script:pass = 0; $script:fail = 0
function Add-Check {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Passed, [string]$Detail = '')
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    Add-Content -LiteralPath $log -Value ("{0}`t{1}`t{2}`t{3}" -f $Name, $status, $Detail, (Get-Date -Format o))
    if ($Passed) { $script:pass++ } else { $script:fail++ }
    Write-Host ("{0,-6} {1}  {2}" -f $status, $Name, $Detail)
}

$exitCode = 1
$hubProcess = $null
$nonce = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $env:TEMP "chopitup_row14_$nonce"
New-Item -ItemType Directory -Path $scratch | Out-Null
$dataDir = Join-Path $scratch 'data'
$dbPath = Join-Path $dataDir 'chopitup.db'

try {
    # --- Step 0: build once, -warnaserror (LESSON M18: incremental is not evidence) -------------------
    Write-Host "Building ChopItUp.slnx (Debug, -warnaserror)..."
    & dotnet build (Join-Path $repoRoot 'ChopItUp.slnx') -c Debug -warnaserror -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $hubExe)) { throw "Expected hub exe not found at '$hubExe' after build." }

    # --- Step 1: load Microsoft.Data.Sqlite straight from the hub's own build output ------------------
    $nativeDir = Join-Path $hubBin 'runtimes\win-x64\native'
    $env:PATH = $nativeDir + ';' + $env:PATH
    Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.core.dll')
    Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.provider.e_sqlite3.dll')
    Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.batteries_v2.dll')
    Add-Type -Path (Join-Path $hubBin 'Microsoft.Data.Sqlite.dll')
    [SQLitePCL.Batteries_V2]::Init()
    Add-Check -Name 'fixture.sqlite-loaded-from-hub-build-output' -Passed $true -Detail $hubBin

    # --- Step 2: fabricate the v11 fixture, raw SQL (v9 cumulative shape + v10's skill_proposals + ----
    #             v11's messages.reply_to_id), transcribed from ChopDb.cs's ApplyV1..ApplyV11 ----------
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

    $roster = @(
        @{ id = 'owner';  name = 'Owner';  kind = 'human'; host = 'human';  model = $null;     classes = $null },
        @{ id = 'claude'; name = 'Claude'; kind = 'model'; host = 'claude'; model = $null;     classes = $null },
        @{ id = 'codex';  name = 'Codex';  kind = 'model'; host = 'codex';  model = $null;     classes = $null },
        @{ id = 'sonnet'; name = 'Sonnet'; kind = 'model'; host = 'claude'; model = 'sonnet';  classes = 'plumbing' },
        @{ id = 'opus';   name = 'Opus';   kind = 'model'; host = 'claude'; model = 'opus';    classes = 'visible,judge' }
    )

    $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath;Mode=ReadWriteCreate;Pooling=False")
    $conn.Open()
    $execCmd = $conn.CreateCommand()
    $execCmd.CommandText = @'
PRAGMA journal_mode=WAL;
CREATE TABLE participants (
    id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL,
    host TEXT, model TEXT, note TEXT, classes TEXT
);
CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL, directory TEXT, archived_at TEXT);
CREATE TABLE messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
    author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL, client_key TEXT,
    reply_to_id INTEGER REFERENCES messages(id)
);
CREATE INDEX ix_messages_room_id ON messages(room_id, id);
CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
CREATE TABLE read_cursors (
    participant_id TEXT NOT NULL REFERENCES participants(id), room_id TEXT NOT NULL REFERENCES rooms(id),
    last_read_id INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (participant_id, room_id)
);
CREATE TABLE memory_proposals (
    id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
    author_id TEXT NOT NULL REFERENCES participants(id), topic TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending', source TEXT, created_at TEXT NOT NULL, decided_at TEXT,
    written_to TEXT, commit_hash TEXT, kind TEXT NOT NULL DEFAULT 'append', replaces TEXT, flags TEXT
);
CREATE INDEX ix_memory_proposals_status ON memory_proposals(status, room_id, id);
CREATE TABLE skills (name TEXT PRIMARY KEY, body_sha256 TEXT NOT NULL, imported_at TEXT NOT NULL, source TEXT);
CREATE TABLE runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
    conductor_id TEXT NOT NULL REFERENCES participants(id), skill_name TEXT NOT NULL, arguments TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL, reason TEXT, cap_spent INTEGER NOT NULL DEFAULT 0, phase TEXT NOT NULL DEFAULT '(start)',
    root_message_id INTEGER NOT NULL, started_at TEXT NOT NULL, parked_at TEXT, parked_seconds INTEGER NOT NULL DEFAULT 0,
    ended_at TEXT, spawns_used INTEGER NOT NULL DEFAULT 0, exchanges INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX ux_runs_one_active_per_room ON runs(room_id) WHERE status = 'active';
CREATE INDEX ix_runs_room ON runs(room_id, id);
CREATE TABLE run_phases (run_id INTEGER NOT NULL REFERENCES runs(id), phase TEXT NOT NULL, entries INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (run_id, phase));
CREATE TABLE run_artifacts (run_id INTEGER NOT NULL REFERENCES runs(id), path TEXT NOT NULL, author_id TEXT NOT NULL REFERENCES participants(id), at TEXT NOT NULL, PRIMARY KEY (run_id, path));
CREATE TABLE run_gate_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER REFERENCES runs(id), room_id TEXT NOT NULL,
    gate TEXT NOT NULL, caller_id TEXT NOT NULL, exit_code INTEGER, outcome TEXT NOT NULL, at TEXT NOT NULL
);
CREATE TABLE skill_files (skill_name TEXT NOT NULL REFERENCES skills(name), path TEXT NOT NULL, sha256 TEXT NOT NULL, PRIMARY KEY (skill_name, path));
CREATE TABLE skill_proposals (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    room_id            TEXT NOT NULL REFERENCES rooms(id),
    author_id          TEXT NOT NULL REFERENCES participants(id),
    name               TEXT NOT NULL,
    source_dir         TEXT NOT NULL,
    tree_sha256        TEXT NOT NULL,
    replaces_installed INTEGER NOT NULL,
    force              INTEGER NOT NULL,
    files              INTEGER NOT NULL,
    bytes              INTEGER NOT NULL,
    status             TEXT NOT NULL DEFAULT 'pending',
    created_at         TEXT NOT NULL,
    decided_at         TEXT,
    installed_at       TEXT
);
CREATE INDEX ix_skill_proposals_status ON skill_proposals(status, room_id, id);
PRAGMA user_version = 11;
'@
    $execCmd.ExecuteNonQuery() | Out-Null

    $tx = $conn.BeginTransaction()

    $pCmd = $conn.CreateCommand(); $pCmd.Transaction = $tx
    $pCmd.CommandText = 'INSERT INTO participants (id, display_name, kind, host, model, note, classes) VALUES ($id,$name,$kind,$host,$model,$note,$classes)'
    foreach ($col in 'id', 'name', 'kind', 'host', 'model', 'note', 'classes') { $pCmd.Parameters.Add('$' + $col, [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null }
    foreach ($p in $roster) {
        foreach ($col in 'id', 'name', 'kind', 'host', 'model', 'classes') { $pCmd.Parameters['$' + $col].Value = if ($null -eq $p[$col]) { [DBNull]::Value } else { $p[$col] } }
        $pCmd.Parameters['$note'].Value = [DBNull]::Value
        $pCmd.ExecuteNonQuery() | Out-Null
    }
    Add-Check -Name 'fixture.participants-seeded' -Passed $true -Detail "$($roster.Count) rows including one spawnable (sonnet)"

    $baseTime = [DateTimeOffset]::new(2026, 1, 1, 8, 0, 0, [TimeSpan]::Zero)
    $rooms = @(
        @{ id = 'general'; name = 'General' },
        @{ id = 'room-2'; name = 'Room 2' }
    )
    $rCmd = $conn.CreateCommand(); $rCmd.Transaction = $tx
    $rCmd.CommandText = 'INSERT INTO rooms (id, name, created_at) VALUES ($id,$name,$at)'
    $rCmd.Parameters.Add('$id', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $rCmd.Parameters.Add('$name', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $rCmd.Parameters.Add('$at', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    foreach ($r in $rooms) {
        $rCmd.Parameters['$id'].Value = $r.id
        $rCmd.Parameters['$name'].Value = $r.name
        $rCmd.Parameters['$at'].Value = $baseTime.ToString('o')
        $rCmd.ExecuteNonQuery() | Out-Null
    }
    Add-Check -Name 'fixture.rooms-seeded' -Passed $true -Detail "$($rooms.Count) rooms"

    $messageCount = 40
    $roomIds = $rooms | ForEach-Object { $_.id }
    $authorIds = @('owner', 'claude', 'sonnet')
    $mCmd = $conn.CreateCommand(); $mCmd.Transaction = $tx
    $mCmd.CommandText = 'INSERT INTO messages (room_id, author_id, body, created_at, client_key) VALUES ($room,$author,$body,$at,$key)'
    $mCmd.Parameters.Add('$room', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $mCmd.Parameters.Add('$author', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $mCmd.Parameters.Add('$body', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $mCmd.Parameters.Add('$at', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $mCmd.Parameters.Add('$key', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    for ($i = 0; $i -lt $messageCount; $i++) {
        $room = $roomIds[$i % $roomIds.Count]
        $author = $authorIds[$i % $authorIds.Count]
        $mCmd.Parameters['$room'].Value = $room
        $mCmd.Parameters['$author'].Value = $author
        $mCmd.Parameters['$body'].Value = "Fabricated row14 dry-run message #$i in $room from $author. Not a real conversation."
        $mCmd.Parameters['$at'].Value = $baseTime.AddSeconds($i).ToString('o')
        $mCmd.Parameters['$key'].Value = if ($i % 5 -eq 0) { "fixture-key-$i" } else { [DBNull]::Value }
        $mCmd.ExecuteNonQuery() | Out-Null
    }
    Add-Check -Name 'fixture.messages-seeded' -Passed $true -Detail "$messageCount messages across $($roomIds.Count) rooms"

    $tx.Commit()

    function Get-Counts([string]$Path) {
        $c = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$Path;Mode=ReadOnly;Pooling=False")
        $c.Open()
        $result = [ordered]@{}
        foreach ($t in 'participants', 'rooms', 'messages') {
            $cmd = $c.CreateCommand(); $cmd.CommandText = "SELECT COUNT(*) FROM $t"
            $result[$t] = [long]$cmd.ExecuteScalar()
        }
        $namesCmd = $c.CreateCommand(); $namesCmd.CommandText = 'SELECT name FROM rooms ORDER BY id'
        $names = New-Object System.Collections.Generic.List[string]
        $r = $namesCmd.ExecuteReader(); while ($r.Read()) { $names.Add($r.GetString(0)) }; $r.Close()
        $result['room_names'] = ($names -join ',')
        $uvCmd = $c.CreateCommand(); $uvCmd.CommandText = 'PRAGMA user_version;'
        $result['user_version'] = [int]$uvCmd.ExecuteScalar()
        $c.Close()
        return $result
    }

    $conn.Close(); $conn.Dispose()
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()

    $before = Get-Counts -Path $dbPath
    Add-Check -Name 'fixture.stamped-v11' -Passed ($before['user_version'] -eq 11) -Detail "user_version=$($before['user_version'])"

    # --- Step 3: seed the scratch hub's own owner token BEFORE its first start -------------------------
    . (Join-Path $repoRoot 'tools\ChopTokenHelpers.ps1')
    $seeded = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')
    $ownerToken = $seeded.owner
    Add-Check -Name 'setup.owner-token-seeded' -Passed ([bool]$ownerToken) -Detail 'OK'

    # --- Step 4: run the REAL hub, launched directly (never dotnet run) --------------------------------
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $hubOutLog = Join-Path $scratch 'hub.out.log'
    $hubErrLog = Join-Path $scratch 'hub.err.log'
    $hubProcess = Start-Process -FilePath $hubExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port") -PassThru -NoNewWindow `
        -RedirectStandardOutput $hubOutLog -RedirectStandardError $hubErrLog

    $hubExeFull = (Resolve-Path $hubExe).Path
    $confirmed = Get-Process -Id $hubProcess.Id -ErrorAction Stop
    if ($confirmed.Path.ToLowerInvariant() -ne $hubExeFull.ToLowerInvariant()) {
        throw "Process $($hubProcess.Id) image path '$($confirmed.Path)' does not match the hub exe this script launched ('$hubExeFull'); refusing to treat it as ours."
    }

    $base = "http://127.0.0.1:$port"
    $health = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($hubProcess.HasExited) { throw "Hub exited early (code $($hubProcess.ExitCode)) while waiting for /health; see $hubErrLog" }
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 5; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $health) { throw "Hub /health did not respond within 30s at $base/health." }
    Add-Check -Name 'hub.launched-directly-not-dotnet-run' -Passed $true -Detail "pid=$($hubProcess.Id) port=$port"
    Add-Check -Name 'health.schema-is-14' -Passed ($health.schema -eq 14) -Detail "schema=$($health.schema)"

    # --- Step 5: the migrated database preserved everything ---------------------------------------------
    $after = Get-Counts -Path $dbPath
    Add-Check -Name 'migrated.stamped-v14' -Passed ($after['user_version'] -eq 14) -Detail "user_version=$($after['user_version'])"
    foreach ($t in 'participants', 'rooms', 'messages') {
        Add-Check -Name "migrated.$t-count-preserved" -Passed ($after[$t] -eq $before[$t]) -Detail "before=$($before[$t]) after=$($after[$t])"
    }
    Add-Check -Name 'migrated.room-names-preserved' -Passed ($after['room_names'] -eq $before['room_names']) -Detail "before=$($before['room_names']) after=$($after['room_names'])"

    $mvc = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath;Mode=ReadOnly;Pooling=False")
    $mvc.Open()
    $roleNullCmd = $mvc.CreateCommand(); $roleNullCmd.CommandText = 'SELECT COUNT(*) FROM participants WHERE role IS NOT NULL'
    $roleNonNull = [long]$roleNullCmd.ExecuteScalar()
    $personaNullCmd = $mvc.CreateCommand(); $personaNullCmd.CommandText = 'SELECT COUNT(*) FROM rooms WHERE persona IS NOT NULL'
    $personaNonNull = [long]$personaNullCmd.ExecuteScalar()
    $roomRolesCmd = $mvc.CreateCommand(); $roomRolesCmd.CommandText = 'SELECT COUNT(*) FROM room_roles'
    $roomRolesCount = [long]$roomRolesCmd.ExecuteScalar()
    $mvc.Close(); $mvc.Dispose()
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    Add-Check -Name 'migrated.role-column-null-on-every-row' -Passed ($roleNonNull -eq 0) -Detail "non-null=$roleNonNull"
    Add-Check -Name 'migrated.persona-column-null-on-every-row' -Passed ($personaNonNull -eq 0) -Detail "non-null=$personaNonNull"
    Add-Check -Name 'migrated.room-roles-table-empty' -Passed ($roomRolesCount -eq 0) -Detail "count=$roomRolesCount"

    # ===== helpers for the API legs =====================================================================

    function Invoke-Owner {
        param([string]$Method = 'GET', [Parameter(Mandatory)][string]$Uri, [switch]$WithAuth, $Body)
        $headers = @{}
        if ($WithAuth) { $headers['Authorization'] = "Bearer $ownerToken" }
        $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; TimeoutSec = 10; SkipHttpErrorCheck = $true }
        if ($null -ne $Body) { $params['ContentType'] = 'application/json'; $params['Body'] = ($Body | ConvertTo-Json) }
        return Invoke-WebRequest @params
    }

    function Get-RoomRoles([string]$RoomId) {
        return Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/roles" -TimeoutSec 10
    }

    # --- Step 6: api legs -- set a persona and a role over the API, read them back ----------------------
    $personaText = 'row14-dryrun persona ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $personaResp = Invoke-Owner -Method Post -Uri "$base/api/rooms/general/persona" -WithAuth -Body @{ persona = $personaText }
    $personaAfter = Get-RoomRoles -RoomId 'general'
    Add-Check -Name 'api.persona-set-and-read-back' -Passed ($personaResp.StatusCode -eq 200 -and $personaAfter.persona -eq $personaText) -Detail "status=$($personaResp.StatusCode)"

    $globalRoleText = 'row14-dryrun global role ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $roleResp = Invoke-Owner -Method Post -Uri "$base/api/participants/sonnet/role" -WithAuth -Body @{ role = $globalRoleText }
    $rolesAfterGlobal = Get-RoomRoles -RoomId 'general'
    $sonnetRow = $rolesAfterGlobal.participants | Where-Object { $_.id -eq 'sonnet' }
    Add-Check -Name 'api.global-role-set-and-read-back' -Passed ($roleResp.StatusCode -eq 200 -and $sonnetRow.effectiveRole -eq $globalRoleText) -Detail "status=$($roleResp.StatusCode)"

    $overrideText = 'row14-dryrun room override ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $overrideResp = Invoke-Owner -Method Post -Uri "$base/api/rooms/general/roles/sonnet" -WithAuth -Body @{ role = $overrideText }
    $rolesAfterOverride = Get-RoomRoles -RoomId 'general'
    $sonnetRow2 = $rolesAfterOverride.participants | Where-Object { $_.id -eq 'sonnet' }
    $overrideOk = ($overrideResp.StatusCode -eq 200) -and ($sonnetRow2.effectiveRole -eq $overrideText) -and ($sonnetRow2.effectiveRole -ne $globalRoleText)
    Add-Check -Name 'api.room-role-set-and-read-back-overrides-global' -Passed $overrideOk -Detail "status=$($overrideResp.StatusCode)"

    # --- Step 7: auth legs -- each of the three write routes refused with no credential, storage unchanged
    $personaBefore2 = (Get-RoomRoles -RoomId 'general').persona
    $noAuthPersona = Invoke-Owner -Method Post -Uri "$base/api/rooms/general/persona" -Body @{ persona = 'should-never-be-stored' }
    $personaAfter2 = (Get-RoomRoles -RoomId 'general').persona
    $refused1 = $noAuthPersona.StatusCode -in @(401, 403)
    Add-Check -Name 'auth.persona-post-refused-without-credential' -Passed ($refused1 -and $personaAfter2 -eq $personaBefore2) -Detail "status=$($noAuthPersona.StatusCode)"

    $globalBefore2 = ((Get-RoomRoles -RoomId 'general').participants | Where-Object { $_.id -eq 'sonnet' }).role
    $noAuthGlobal = Invoke-Owner -Method Post -Uri "$base/api/participants/sonnet/role" -Body @{ role = 'should-never-be-stored' }
    $globalAfter2 = ((Get-RoomRoles -RoomId 'general').participants | Where-Object { $_.id -eq 'sonnet' }).role
    $refused2 = $noAuthGlobal.StatusCode -in @(401, 403)
    Add-Check -Name 'auth.global-role-post-refused-without-credential' -Passed ($refused2 -and $globalAfter2 -eq $globalBefore2) -Detail "status=$($noAuthGlobal.StatusCode)"

    $roomRoleBefore2 = ((Get-RoomRoles -RoomId 'general').participants | Where-Object { $_.id -eq 'sonnet' }).roomRole
    $noAuthRoomRole = Invoke-Owner -Method Post -Uri "$base/api/rooms/general/roles/sonnet" -Body @{ role = 'should-never-be-stored' }
    $roomRoleAfter2 = ((Get-RoomRoles -RoomId 'general').participants | Where-Object { $_.id -eq 'sonnet' }).roomRole
    $refused3 = $noAuthRoomRole.StatusCode -in @(401, 403)
    Add-Check -Name 'auth.room-role-post-refused-without-credential' -Passed ($refused3 -and $roomRoleAfter2 -eq $roomRoleBefore2) -Detail "status=$($noAuthRoomRole.StatusCode)"

    # --- Step 8: live-edit leg -- a role changed while the hub keeps running is visible in the next GET,
    #             with no restart (AC7; the test a startup-snapshot implementation fails) ----------------
    $secondRoleText = 'row14-dryrun SECOND global role ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    Invoke-Owner -Method Post -Uri "$base/api/participants/opus/role" -WithAuth -Body @{ role = 'row14-dryrun-first-opus-role' } | Out-Null
    $firstRead = (Get-RoomRoles -RoomId 'general').participants | Where-Object { $_.id -eq 'opus' }
    Invoke-Owner -Method Post -Uri "$base/api/participants/opus/role" -WithAuth -Body @{ role = $secondRoleText } | Out-Null
    $secondRead = (Get-RoomRoles -RoomId 'general').participants | Where-Object { $_.id -eq 'opus' }
    $liveEditOk = ($firstRead.effectiveRole -eq 'row14-dryrun-first-opus-role') -and ($secondRead.effectiveRole -eq $secondRoleText) -and (-not $hubProcess.HasExited)
    Add-Check -Name 'live-edit.role-change-visible-without-restart' -Passed $liveEditOk -Detail "hub-pid=$($hubProcess.Id) still-running=$(-not $hubProcess.HasExited)"

    # --- Step 9: stop the hub before the raw FK leg (avoid contending with its own WAL writer) ----------
    Write-Host "Stopping hub pid $($hubProcess.Id)..."
    Stop-Process -Id $hubProcess.Id
    $hubProcess.WaitForExit(15000) | Out-Null
    if (-not $hubProcess.HasExited) { throw "Hub process $($hubProcess.Id) did not exit within 15s of Stop-Process." }
    $hubProcess = $null

    # --- Step 10: fk leg -- an override insert naming a non-existent room fails at the database ---------
    #              (ChopDb.Open sets PRAGMA foreign_keys=ON on every serving connection, ledger 20;
    #              this ad-hoc connection sets it explicitly to prove the constraint itself, not the
    #              store's own existence-check short-circuit, is what refuses it)
    $fkThrew = $false
    $fkConn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath;Mode=ReadWrite;Pooling=False")
    try {
        $fkConn.Open()
        $pragmaCmd = $fkConn.CreateCommand(); $pragmaCmd.CommandText = 'PRAGMA foreign_keys=ON;'; $pragmaCmd.ExecuteNonQuery() | Out-Null
        $insCmd = $fkConn.CreateCommand()
        $insCmd.CommandText = "INSERT INTO room_roles (room_id, participant_id, role) VALUES ('row14-nonexistent-room-xyz', 'sonnet', 'x')"
        $insCmd.ExecuteNonQuery() | Out-Null
    } catch [Microsoft.Data.Sqlite.SqliteException] {
        $fkThrew = $true
    } finally {
        $fkConn.Close(); $fkConn.Dispose()
        [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    }
    Add-Check -Name 'fk.room-roles-insert-for-nonexistent-room-refused' -Passed $fkThrew -Detail "threw=$fkThrew"

    $exitCode = if ($script:fail -eq 0) { 0 } else { 1 }
}
catch {
    Add-Check -Name 'unhandled-error' -Passed $false -Detail "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber)]"
    $exitCode = 1
}
finally {
    if ($null -ne $hubProcess) {
        try {
            if (-not $hubProcess.HasExited) {
                Write-Host "Cleanup: stopping hub pid $($hubProcess.Id) after an earlier failure..."
                Stop-Process -Id $hubProcess.Id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }

    if ($KeepEvidence) {
        try { Copy-Item -Path $scratch -Destination (Join-Path $evidenceDir 'scratch') -Recurse -Force -ErrorAction Stop } catch { }
        Write-Host "Scratch evidence kept at: $(Join-Path $evidenceDir 'scratch')"
    }
    try { Remove-Item -Path $scratch -Recurse -Force -ErrorAction Stop } catch { Write-Host "Warning: could not clean up '$scratch': $($_.Exception.Message)" }

    Write-Host ""
    Write-Host ('-' * 60)
    Write-Host ("Row 14 self-check: {0} PASS, {1} FAIL -- log: {2}" -f $script:pass, $script:fail, $log)
    Write-Host "Evidence directory: $evidenceDir"
}

exit $exitCode
