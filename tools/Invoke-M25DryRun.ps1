<#
.SYNOPSIS
    Row 25 (M25 task 9) migration dry run: fabricates a real-shape v9 ChopItUp database (every table
    and column the v1-v9 migration ladder in ChopDb.cs produces, seeded with realistic-looking
    fabricated data), runs the REAL built hub against it, and proves the v9 -> v10 step (schema 10,
    task 3's `skill_proposals` table) came out right without losing or corrupting anything that was
    already there. Fabricated data only -- never a copy of anything real.

.DESCRIPTION
    "In the style of Invoke-M2DryRun.ps1": build once, fabricate the PREVIOUS schema version's
    on-disk shape directly (never via ChopDb, which can no longer produce anything older than its own
    LatestSchemaVersion), launch the real ChopItUp.Hub.exe (never `dotnet run` -- same PID-identity
    reasoning M2's script documents), and assert before/after row counts plus the verified backup.

    One deliberate difference from M2's mechanism: `tools/ChopItUp.Corpus` (M2DryRun's fixture writer)
    is hard-pinned to schema 1/2 BY DESIGN ("this tool must keep describing the OLD (v1) on-disk shape
    even after ChopDb stops being able to produce one" -- CorpusBuilder.cs's own doc comment) and
    v9's cumulative shape (nine migration steps: participants.host/model/note/classes, rooms.directory/
    archived_at, messages.client_key, memory_proposals plus its v9 kind/replaces/flags columns, skills,
    runs/run_phases/run_artifacts/run_gate_runs/skill_files) is far outside that tool's scope to add
    without breaking its own v1/v2 contract. This script instead loads the REAL, already-built
    Microsoft.Data.Sqlite.dll straight out of the hub's own bin output (Add-Type, with the matching
    runtimes\win-x64\native\e_sqlite3.dll prepended onto PATH so the native provider resolves outside
    the hub's own AppContext.BaseDirectory) and writes the v9 fixture with raw SQL, transcribed
    directly from ChopDb.cs's ApplyV1..ApplyV9 -- the same "raw SQL, never via ChopDb" stance
    CorpusBuilder itself takes, just executed in this script rather than a second console tool.

    Every path this script touches is rooted under a fresh $env:TEMP scratch directory with a GUID
    nonce -- never C:\Self Apps, never a real data directory. Never touches tokens.json meaningfully
    (the hub mints a fresh one against this scratch data dir; nothing here reads a real one).

.PARAMETER KeepEvidence
    Keep the scratch directory (fixture database, backup, hub stdout/stderr, logs) instead of deleting
    it at the end. Prints the directory path either way it exits.
#>
[CmdletBinding()]
param(
    [switch]$KeepEvidence
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$slnPath = Join-Path $repoRoot 'ChopItUp.slnx'
$hubBin = Join-Path $repoRoot 'src\ChopItUp.Hub\bin\Debug\net10.0'
$hubExe = Join-Path $hubBin 'ChopItUp.Hub.exe'

$nonce = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $env:TEMP "chopitup_m25dryrun_$nonce"
New-Item -ItemType Directory -Path $scratch | Out-Null
$dataDir = Join-Path $scratch 'data'
$dbPath = Join-Path $dataDir 'chopitup.db'

$checkLines = New-Object System.Collections.Generic.List[string]
$failCount = 0

function Add-Check {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Passed, [string]$Detail = '')
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $line = "$status  $Name  $Detail".TrimEnd()
    $checkLines.Add($line)
    if (-not $Passed) { $script:failCount++ }
    Write-Host $line
}

$exitCode = 1
$hubProcess = $null

try {
    # --- Step 0: build once (the CI/repo strictness gate) ------------------------------------------
    Write-Host "Building $slnPath (Debug, -warnaserror)..."
    & dotnet build $slnPath -c Debug -warnaserror -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $hubExe)) { throw "Expected hub exe not found at '$hubExe' after build." }

    # --- Step 1: load Microsoft.Data.Sqlite straight from the hub's own build output ---------------
    # The e_sqlite3 native provider is resolved by the OS loader's normal PATH search, so its
    # directory has to be visible to THIS process (pwsh.exe), not just to the hub's own
    # AppContext.BaseDirectory -- prepending it is what makes Batteries_V2.Init() succeed here
    # (measured directly against this repo's build output before writing the rest of this script).
    $nativeDir = Join-Path $hubBin 'runtimes\win-x64\native'
    $env:PATH = $nativeDir + ';' + $env:PATH
    Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.core.dll')
    Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.provider.e_sqlite3.dll')
    Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.batteries_v2.dll')
    Add-Type -Path (Join-Path $hubBin 'Microsoft.Data.Sqlite.dll')
    [SQLitePCL.Batteries_V2]::Init()
    Add-Check -Name 'sqlite.loaded-from-hub-build-output' -Passed $true -Detail $hubBin

    # --- Step 2: fabricate the v9 fixture, raw SQL, transcribed from ChopDb.cs's ApplyV1..ApplyV9 ---
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    $roster = @(
        @{ id='owner';         name='Owner';         kind='human';  host='human';  model=$null;          note=$null;                                                                                       classes=$null },
        @{ id='claude';        name='Claude';        kind='model';  host='claude'; model=$null;          note='App-backed: Claude Desktop or Claude Code, whatever model the app has selected.';           classes=$null },
        @{ id='codex';         name='Codex';         kind='model';  host='codex';  model=$null;          note='App-backed: the Codex app or CLI, whatever model the app has selected.';                    classes=$null },
        @{ id='opus';          name='Opus';          kind='model';  host='claude'; model='opus';         note=$null;                                                                                       classes='visible,judge' },
        @{ id='sonnet';        name='Sonnet';        kind='model';  host='claude'; model='sonnet';       note=$null;                                                                                       classes='plumbing' },
        @{ id='fable';         name='Fable';         kind='model';  host='claude'; model='fable';        note='May bill to usage credits instead of the plan''s included limits.';                          classes='judge' },
        @{ id='gpt-6-astra';   name='GPT-6 Astra';   kind='model';  host='codex';  model='gpt-6-astra';  note=$null;                                                                                       classes=$null },
        @{ id='gpt-5.6-sol';   name='GPT-5.6 Sol';   kind='model';  host='codex';  model='gpt-5.6-sol';  note=$null;                                                                                       classes=$null },
        @{ id='gpt-5.6-terra'; name='GPT-5.6 Terra'; kind='model';  host='codex';  model='gpt-5.6-terra';note=$null;                                                                                       classes=$null },
        @{ id='gpt-5.6-luna';  name='GPT-5.6 Luna';  kind='model';  host='codex';  model='gpt-5.6-luna'; note=$null;                                                                                       classes=$null },
        @{ id='gpt-5.5';       name='GPT-5.5';       kind='model';  host='codex';  model='gpt-5.5';      note=$null;                                                                                       classes=$null },
        @{ id='gpt-5.4-mini';  name='GPT-5.4 Mini';  kind='model';  host='codex';  model='gpt-5.4-mini'; note=$null;                                                                                       classes=$null },
        @{ id='hub';           name='Hub';           kind='system'; host='hub';    model=$null;          note='The hub itself. Posts exchange notes: timeouts, budget, conclusions. Cannot be mentioned or spawned.'; classes=$null },
        @{ id='owner-remote';  name='Owner (remote)';kind='human';  host='human';  model=$null;          note='The owner, posting from a session on another device. Same authority as owner; the hub stamps which hand typed.'; classes=$null }
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
    author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL, client_key TEXT
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
PRAGMA user_version = 9;
'@
    $execCmd.ExecuteNonQuery() | Out-Null

    $tx = $conn.BeginTransaction()

    $pCmd = $conn.CreateCommand(); $pCmd.Transaction = $tx
    $pCmd.CommandText = 'INSERT INTO participants (id, display_name, kind, host, model, note, classes) VALUES ($id,$name,$kind,$host,$model,$note,$classes)'
    foreach ($col in 'id', 'name', 'kind', 'host', 'model', 'note', 'classes') { $pCmd.Parameters.Add('$' + $col, [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null }
    foreach ($p in $roster) {
        foreach ($col in 'id', 'name', 'kind', 'host', 'model', 'note', 'classes') {
            $val = $p[$col]
            $pCmd.Parameters['$' + $col].Value = if ($null -eq $val) { [DBNull]::Value } else { $val }
        }
        $pCmd.ExecuteNonQuery() | Out-Null
    }
    Add-Check -Name 'fixture.participants-seeded' -Passed $true -Detail "$($roster.Count) rows, full v7+ roster"

    $baseTime = [DateTimeOffset]::new(2026, 1, 1, 8, 0, 0, [TimeSpan]::Zero)
    $rooms = @(
        @{ id = 'general'; name = 'General'; directory = $null; archived = $false },
        @{ id = 'room-2'; name = 'Room 2'; directory = (Join-Path $scratch 'fixture-room-2'); archived = $false },
        @{ id = 'room-3'; name = 'Room 3 (archived)'; directory = $null; archived = $true }
    )
    $rCmd = $conn.CreateCommand(); $rCmd.Transaction = $tx
    $rCmd.CommandText = 'INSERT INTO rooms (id, name, created_at, directory, archived_at) VALUES ($id,$name,$at,$dir,$arch)'
    $rCmd.Parameters.Add('$id', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $rCmd.Parameters.Add('$name', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $rCmd.Parameters.Add('$at', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $rCmd.Parameters.Add('$dir', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $rCmd.Parameters.Add('$arch', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    foreach ($r in $rooms) {
        $rCmd.Parameters['$id'].Value = $r.id
        $rCmd.Parameters['$name'].Value = $r.name
        $rCmd.Parameters['$at'].Value = $baseTime.ToString('o')
        $rCmd.Parameters['$dir'].Value = if ($r.directory) { $r.directory } else { [DBNull]::Value }
        $rCmd.Parameters['$arch'].Value = if ($r.archived) { $baseTime.AddDays(1).ToString('o') } else { [DBNull]::Value }
        $rCmd.ExecuteNonQuery() | Out-Null
    }
    Add-Check -Name 'fixture.rooms-seeded' -Passed $true -Detail "$($rooms.Count) rooms, one with a directory, one archived"

    $messageCount = 300
    $roomIds = $rooms | ForEach-Object { $_.id }
    $authorIds = @('owner', 'claude', 'codex')
    $roomMessageIds = @{}
    foreach ($rid in $roomIds) { $roomMessageIds[$rid] = New-Object System.Collections.Generic.List[long] }

    $mCmd = $conn.CreateCommand(); $mCmd.Transaction = $tx
    $mCmd.CommandText = 'INSERT INTO messages (room_id, author_id, body, created_at, client_key) VALUES ($room,$author,$body,$at,$key); SELECT last_insert_rowid();'
    $mCmd.Parameters.Add('$room', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $mCmd.Parameters.Add('$author', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $mCmd.Parameters.Add('$body', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $mCmd.Parameters.Add('$at', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $mCmd.Parameters.Add('$key', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $bodyLenSumBefore = [long]0
    for ($i = 0; $i -lt $messageCount; $i++) {
        $room = $roomIds[$i % $roomIds.Count]
        $author = $authorIds[$i % $authorIds.Count]
        $body = "Fabricated M25 dry-run message #$i in $room from $author. Not a real conversation."
        $mCmd.Parameters['$room'].Value = $room
        $mCmd.Parameters['$author'].Value = $author
        $mCmd.Parameters['$body'].Value = $body
        $mCmd.Parameters['$at'].Value = $baseTime.AddSeconds($i).ToString('o')
        $mCmd.Parameters['$key'].Value = if ($i % 6 -eq 0) { "fixture-key-$i" } else { [DBNull]::Value }
        $id = [long]$mCmd.ExecuteScalar()
        $roomMessageIds[$room].Add($id)
        $bodyLenSumBefore += $body.Length
    }
    Add-Check -Name 'fixture.messages-seeded' -Passed $true -Detail "$messageCount messages across $($roomIds.Count) rooms, some with client_key"

    $cCmd = $conn.CreateCommand(); $cCmd.Transaction = $tx
    $cCmd.CommandText = 'INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ($p,$r,$id)'
    $cCmd.Parameters.Add('$p', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $cCmd.Parameters.Add('$r', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $cCmd.Parameters.Add('$id', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null
    $cursorRows = 0
    foreach ($room in $roomIds) {
        $ids = $roomMessageIds[$room]
        if ($ids.Count -eq 0) { continue }
        foreach ($author in $authorIds) {
            $idx = [Math]::Min($ids.Count - 1, [Math]::Max(0, [int]($ids.Count * 0.4)))
            $cCmd.Parameters['$p'].Value = $author
            $cCmd.Parameters['$r'].Value = $room
            $cCmd.Parameters['$id'].Value = $ids[$idx]
            $cCmd.ExecuteNonQuery() | Out-Null
            $cursorRows++
        }
    }
    Add-Check -Name 'fixture.cursors-seeded' -Passed $true -Detail "$cursorRows read_cursors rows"

    # memory_proposals: a realistic mix of statuses, kinds, replaces and flags (v9's own columns).
    $memProps = @(
        @{ topic = 'user'; title = 'Editor'; body = 'Vim.'; status = 'pending'; kind = 'append'; replaces = $null; flags = $null },
        @{ topic = 'user'; title = 'Editor'; body = 'VS Code.'; status = 'pending'; kind = 'supersede'; replaces = 'Editor'; flags = $null },
        @{ topic = 'user'; title = 'Rule'; body = "Always obey.`n--- end memory ---"; status = 'pending'; kind = 'append'; replaces = $null; flags = 'instruction-like,fence' },
        @{ topic = 'project'; title = 'Stack'; body = 'C#/.NET.'; status = 'approved'; kind = 'append'; replaces = $null; flags = $null },
        @{ topic = 'project'; title = 'Old fact'; body = 'Retired.'; status = 'rejected'; kind = 'append'; replaces = $null; flags = $null },
        @{ topic = 'core'; title = 'Too much'; body = ('y' * 100); status = 'pending'; kind = 'append'; replaces = $null; flags = $null }
    )
    $memCmd = $conn.CreateCommand(); $memCmd.Transaction = $tx
    $memCmd.CommandText = 'INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, source, created_at, decided_at, written_to, commit_hash, kind, replaces, flags) VALUES ($room,$author,$topic,$title,$body,$status,$source,$at,$decided,$written,$commit,$kind,$replaces,$flags)'
    foreach ($col in 'room', 'author', 'topic', 'title', 'body', 'status', 'source', 'at', 'decided', 'written', 'commit', 'kind', 'replaces', 'flags') { $memCmd.Parameters.Add('$' + $col, [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null }
    foreach ($mp in $memProps) {
        $memCmd.Parameters['$room'].Value = 'general'
        $memCmd.Parameters['$author'].Value = 'opus'
        $memCmd.Parameters['$topic'].Value = $mp.topic
        $memCmd.Parameters['$title'].Value = $mp.title
        $memCmd.Parameters['$body'].Value = $mp.body
        $memCmd.Parameters['$status'].Value = $mp.status
        $memCmd.Parameters['$source'].Value = [DBNull]::Value
        $memCmd.Parameters['$at'].Value = $baseTime.ToString('o')
        $memCmd.Parameters['$decided'].Value = if ($mp.status -eq 'pending') { [DBNull]::Value } else { $baseTime.AddMinutes(5).ToString('o') }
        $memCmd.Parameters['$written'].Value = [DBNull]::Value
        $memCmd.Parameters['$commit'].Value = [DBNull]::Value
        $memCmd.Parameters['$kind'].Value = $mp.kind
        $memCmd.Parameters['$replaces'].Value = if ($mp.replaces) { $mp.replaces } else { [DBNull]::Value }
        $memCmd.Parameters['$flags'].Value = if ($mp.flags) { $mp.flags } else { [DBNull]::Value }
        $memCmd.ExecuteNonQuery() | Out-Null
    }
    Add-Check -Name 'fixture.memory-proposals-seeded' -Passed $true -Detail "$($memProps.Count) rows, mixed status/kind/replaces/flags"

    # skills + skill_files (v7/v8).
    $skillRows = @(
        @{ name = 'fixture-skill-one'; hash = ('a' * 64) },
        @{ name = 'fixture-skill-two'; hash = ('b' * 64) }
    )
    $skCmd = $conn.CreateCommand(); $skCmd.Transaction = $tx
    $skCmd.CommandText = 'INSERT INTO skills (name, body_sha256, imported_at, source) VALUES ($name,$hash,$at,$source)'
    $skCmd.Parameters.Add('$name', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $skCmd.Parameters.Add('$hash', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $skCmd.Parameters.Add('$at', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $skCmd.Parameters.Add('$source', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $sfCmd = $conn.CreateCommand(); $sfCmd.Transaction = $tx
    $sfCmd.CommandText = 'INSERT INTO skill_files (skill_name, path, sha256) VALUES ($name,$path,$sha)'
    $sfCmd.Parameters.Add('$name', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $sfCmd.Parameters.Add('$path', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $sfCmd.Parameters.Add('$sha', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $skillFileRows = 0
    foreach ($s in $skillRows) {
        $skCmd.Parameters['$name'].Value = $s.name
        $skCmd.Parameters['$hash'].Value = $s.hash
        $skCmd.Parameters['$at'].Value = $baseTime.ToString('o')
        $skCmd.Parameters['$source'].Value = [DBNull]::Value
        $skCmd.ExecuteNonQuery() | Out-Null
        foreach ($rel in @('SKILL.md', 'scripts/gate.ps1')) {
            $sfCmd.Parameters['$name'].Value = $s.name
            $sfCmd.Parameters['$path'].Value = $rel
            $sfCmd.Parameters['$sha'].Value = ('c' * 64)
            $sfCmd.ExecuteNonQuery() | Out-Null
            $skillFileRows++
        }
    }
    Add-Check -Name 'fixture.skills-seeded' -Passed $true -Detail "$($skillRows.Count) skills, $skillFileRows skill_files rows"

    # runs + satellites (v8), all status 'ended' so HubHost's start-up park sweep leaves them alone.
    $runCmd = $conn.CreateCommand(); $runCmd.Transaction = $tx
    $runCmd.CommandText = 'INSERT INTO runs (room_id, conductor_id, skill_name, arguments, status, reason, cap_spent, phase, root_message_id, started_at, ended_at, spawns_used, exchanges) VALUES ($room,$conductor,$skill,$args,$status,$reason,$cap,$phase,$root,$started,$ended,$spawns,$exchanges); SELECT last_insert_rowid();'
    foreach ($col in 'room', 'conductor', 'skill', 'args', 'status', 'reason', 'phase', 'started', 'ended') { $runCmd.Parameters.Add('$' + $col, [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null }
    $runCmd.Parameters.Add('$cap', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null
    $runCmd.Parameters.Add('$root', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null
    $runCmd.Parameters.Add('$spawns', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null
    $runCmd.Parameters.Add('$exchanges', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null

    $phaseCmd = $conn.CreateCommand(); $phaseCmd.Transaction = $tx
    $phaseCmd.CommandText = 'INSERT INTO run_phases (run_id, phase, entries) VALUES ($id,$phase,$entries)'
    $phaseCmd.Parameters.Add('$id', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null
    $phaseCmd.Parameters.Add('$phase', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $phaseCmd.Parameters.Add('$entries', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null

    $artCmd = $conn.CreateCommand(); $artCmd.Transaction = $tx
    $artCmd.CommandText = 'INSERT INTO run_artifacts (run_id, path, author_id, at) VALUES ($id,$path,$author,$at)'
    $artCmd.Parameters.Add('$id', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null
    $artCmd.Parameters.Add('$path', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $artCmd.Parameters.Add('$author', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $artCmd.Parameters.Add('$at', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null

    $gateCmd = $conn.CreateCommand(); $gateCmd.Transaction = $tx
    $gateCmd.CommandText = 'INSERT INTO run_gate_runs (run_id, room_id, gate, caller_id, exit_code, outcome, at) VALUES ($id,$room,$gate,$caller,$exit,$outcome,$at)'
    $gateCmd.Parameters.Add('$id', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null
    $gateCmd.Parameters.Add('$room', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $gateCmd.Parameters.Add('$gate', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $gateCmd.Parameters.Add('$caller', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $gateCmd.Parameters.Add('$exit', [Microsoft.Data.Sqlite.SqliteType]::Integer) | Out-Null
    $gateCmd.Parameters.Add('$outcome', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null
    $gateCmd.Parameters.Add('$at', [Microsoft.Data.Sqlite.SqliteType]::Text) | Out-Null

    $runCount = 2
    for ($i = 1; $i -le $runCount; $i++) {
        $runCmd.Parameters['$room'].Value = 'general'
        $runCmd.Parameters['$conductor'].Value = 'sonnet'
        $runCmd.Parameters['$skill'].Value = "fixture-skill-$i"
        $runCmd.Parameters['$args'].Value = ''
        $runCmd.Parameters['$status'].Value = 'ended'
        $runCmd.Parameters['$reason'].Value = 'finished'
        $runCmd.Parameters['$cap'].Value = 3
        $runCmd.Parameters['$phase'].Value = '(done)'
        $runCmd.Parameters['$root'].Value = $roomMessageIds['general'][0]
        $runCmd.Parameters['$started'].Value = $baseTime.ToString('o')
        $runCmd.Parameters['$ended'].Value = $baseTime.AddMinutes(10).ToString('o')
        $runCmd.Parameters['$spawns'].Value = 3
        $runCmd.Parameters['$exchanges'].Value = 1
        $runId = [long]$runCmd.ExecuteScalar()

        $phaseCmd.Parameters['$id'].Value = $runId
        $phaseCmd.Parameters['$phase'].Value = 'implement'
        $phaseCmd.Parameters['$entries'].Value = 1
        $phaseCmd.ExecuteNonQuery() | Out-Null

        $artCmd.Parameters['$id'].Value = $runId
        $artCmd.Parameters['$path'].Value = "fixture/artifact-$i.md"
        $artCmd.Parameters['$author'].Value = 'sonnet'
        $artCmd.Parameters['$at'].Value = $baseTime.AddMinutes(5).ToString('o')
        $artCmd.ExecuteNonQuery() | Out-Null

        $gateCmd.Parameters['$id'].Value = $runId
        $gateCmd.Parameters['$room'].Value = 'general'
        $gateCmd.Parameters['$gate'].Value = 'gate'
        $gateCmd.Parameters['$caller'].Value = 'sonnet'
        $gateCmd.Parameters['$exit'].Value = 0
        $gateCmd.Parameters['$outcome'].Value = 'passed'
        $gateCmd.Parameters['$at'].Value = $baseTime.AddMinutes(6).ToString('o')
        $gateCmd.ExecuteNonQuery() | Out-Null
    }
    Add-Check -Name 'fixture.runs-seeded' -Passed $true -Detail "$runCount ended runs with one phase/artifact/gate-run each"

    $tx.Commit()

    # Before-migration counts, read back through a fresh short-lived connection (mirrors
    # CorpusBuilder.FingerprintOf: safe to call while another process might later hold the file).
    function Get-Counts([string]$Path) {
        $c = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$Path;Mode=ReadOnly;Pooling=False")
        $c.Open()
        $result = [ordered]@{}
        foreach ($t in 'participants', 'rooms', 'messages', 'read_cursors', 'memory_proposals', 'skills', 'skill_files', 'runs', 'run_phases', 'run_artifacts', 'run_gate_runs') {
            $cmd = $c.CreateCommand(); $cmd.CommandText = "SELECT COUNT(*) FROM $t"
            $result[$t] = [long]$cmd.ExecuteScalar()
        }
        $bodyCmd = $c.CreateCommand(); $bodyCmd.CommandText = 'SELECT COALESCE(SUM(LENGTH(body)),0) FROM messages'
        $result['messages_body_len_sum'] = [long]$bodyCmd.ExecuteScalar()
        $uvCmd = $c.CreateCommand(); $uvCmd.CommandText = 'PRAGMA user_version;'
        $result['user_version'] = [int]$uvCmd.ExecuteScalar()
        $c.Close()
        return $result
    }

    $conn.Close()
    $conn.Dispose()
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()

    $before = Get-Counts -Path $dbPath
    Add-Check -Name 'fixture.stamped-v9' -Passed ($before['user_version'] -eq 9) -Detail "user_version=$($before['user_version'])"

    # --- Step 3: run the REAL hub, launched directly (never dotnet run) ----------------------------
    $port = 8830
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
    Add-Check -Name 'health.schema-is-10' -Passed ($health.schema -eq 10) -Detail "schema=$($health.schema)"

    Write-Host "Stopping hub pid $($hubProcess.Id)..."
    Stop-Process -Id $hubProcess.Id
    $hubProcess.WaitForExit(15000) | Out-Null
    if (-not $hubProcess.HasExited) { throw "Hub process $($hubProcess.Id) did not exit within 15s of Stop-Process." }
    $hubProcess = $null

    # --- Step 4: the backup -------------------------------------------------------------------------
    Add-Type -Path (Join-Path $hubBin 'SQLitePCLRaw.core.dll') -ErrorAction SilentlyContinue
    $bakFiles = @(Get-ChildItem -Path $dataDir -Filter 'chopitup.db.v9.*.bak' -File)
    Add-Check -Name 'backup.count-is-one' -Passed ($bakFiles.Count -eq 1) -Detail "count=$($bakFiles.Count)"
    if ($bakFiles.Count -eq 1) {
        $bakPath = $bakFiles[0].FullName
        $bc = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$bakPath;Mode=ReadOnly;Pooling=False")
        $bc.Open()
        $qc = $bc.CreateCommand(); $qc.CommandText = 'PRAGMA quick_check;'
        $quickCheck = $qc.ExecuteScalar()
        $uv = $bc.CreateCommand(); $uv.CommandText = 'PRAGMA user_version;'
        $bakVersion = [int]$uv.ExecuteScalar()
        $mc = $bc.CreateCommand(); $mc.CommandText = 'SELECT COUNT(*) FROM messages'
        $bakMessages = [long]$mc.ExecuteScalar()
        $bc.Close()
        Add-Check -Name 'backup.quick-check-ok' -Passed ($quickCheck -eq 'ok') -Detail "quick_check=$quickCheck"
        Add-Check -Name 'backup.stamped-v9' -Passed ($bakVersion -eq 9) -Detail "user_version=$bakVersion"
        Add-Check -Name 'backup.message-count-matches-pre-migration' -Passed ($bakMessages -eq $before['messages']) -Detail "backup=$bakMessages before=$($before['messages'])"
    }
    else {
        Add-Check -Name 'backup.quick-check-ok' -Passed $false -Detail 'skipped: backup.count-is-one failed'
        Add-Check -Name 'backup.stamped-v9' -Passed $false -Detail 'skipped: backup.count-is-one failed'
        Add-Check -Name 'backup.message-count-matches-pre-migration' -Passed $false -Detail 'skipped: backup.count-is-one failed'
    }

    # --- Step 5: the migrated database -----------------------------------------------------------
    $after = Get-Counts -Path $dbPath
    Add-Check -Name 'migrated.stamped-v10' -Passed ($after['user_version'] -eq 10) -Detail "user_version=$($after['user_version'])"
    foreach ($t in 'participants', 'rooms', 'messages', 'read_cursors', 'memory_proposals', 'skills', 'skill_files', 'runs', 'run_phases', 'run_artifacts', 'run_gate_runs') {
        Add-Check -Name "migrated.$t-count-preserved" -Passed ($after[$t] -eq $before[$t]) -Detail "before=$($before[$t]) after=$($after[$t])"
    }
    Add-Check -Name 'migrated.messages-body-bytes-preserved' -Passed ($after['messages_body_len_sum'] -eq $before['messages_body_len_sum']) `
        -Detail "before=$($before['messages_body_len_sum']) after=$($after['messages_body_len_sum'])"

    # v9's own columns (kind/replaces/flags) survived on the exact rows that carried them.
    $mvc = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath;Mode=ReadOnly;Pooling=False")
    $mvc.Open()
    $mcmd = $mvc.CreateCommand()
    $mcmd.CommandText = "SELECT COUNT(*) FROM memory_proposals WHERE kind = 'supersede' AND replaces = 'Editor'"
    $supersedeCount = [long]$mcmd.ExecuteScalar()
    $fcmd = $mvc.CreateCommand()
    $fcmd.CommandText = "SELECT COUNT(*) FROM memory_proposals WHERE flags = 'instruction-like,fence'"
    $flagsCount = [long]$fcmd.ExecuteScalar()
    Add-Check -Name 'migrated.memory-proposals-supersede-replaces-preserved' -Passed ($supersedeCount -eq 1) -Detail "count=$supersedeCount"
    Add-Check -Name 'migrated.memory-proposals-flags-preserved' -Passed ($flagsCount -eq 1) -Detail "count=$flagsCount"

    # The new v10 table: present, empty, and shaped as ApplyV10 declares it.
    $stCmd = $mvc.CreateCommand(); $stCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='skill_proposals'"
    $tableExists = ([long]$stCmd.ExecuteScalar()) -eq 1
    Add-Check -Name 'migrated.skill-proposals-table-created' -Passed $tableExists -Detail "exists=$tableExists"
    if ($tableExists) {
        $cntCmd = $mvc.CreateCommand(); $cntCmd.CommandText = 'SELECT COUNT(*) FROM skill_proposals'
        $spCount = [long]$cntCmd.ExecuteScalar()
        Add-Check -Name 'migrated.skill-proposals-empty' -Passed ($spCount -eq 0) -Detail "count=$spCount"

        $colCmd = $mvc.CreateCommand(); $colCmd.CommandText = "SELECT name FROM pragma_table_info('skill_proposals') ORDER BY cid"
        $cols = New-Object System.Collections.Generic.List[string]
        $reader = $colCmd.ExecuteReader()
        while ($reader.Read()) { $cols.Add($reader.GetString(0)) }
        $reader.Close()
        $expectedCols = @('id', 'room_id', 'author_id', 'name', 'source_dir', 'tree_sha256', 'replaces_installed', 'force', 'files', 'bytes', 'status', 'created_at', 'decided_at', 'installed_at')
        $colsMatch = ($cols.Count -eq $expectedCols.Count) -and (@(0..($expectedCols.Count - 1)) | ForEach-Object { $cols[$_] -eq $expectedCols[$_] }) -notcontains $false
        Add-Check -Name 'migrated.skill-proposals-columns-match-ApplyV10' -Passed $colsMatch -Detail ($cols -join ',')

        $idxCmd = $mvc.CreateCommand(); $idxCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_skill_proposals_status'"
        Add-Check -Name 'migrated.skill-proposals-index-created' -Passed (([long]$idxCmd.ExecuteScalar()) -eq 1) -Detail ''
    }
    else {
        Add-Check -Name 'migrated.skill-proposals-empty' -Passed $false -Detail 'skipped: table not created'
        Add-Check -Name 'migrated.skill-proposals-columns-match-ApplyV10' -Passed $false -Detail 'skipped: table not created'
        Add-Check -Name 'migrated.skill-proposals-index-created' -Passed $false -Detail 'skipped: table not created'
    }
    $mvc.Close()
    $mvc.Dispose()
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()

    $exitCode = if ($failCount -eq 0) { 0 } else { 1 }
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
        }
        catch { }
    }

    $logPath = Join-Path $scratch 'm25-dryrun.log'
    $checkLines | Set-Content -Path $logPath -Encoding utf8

    $passCount = ($checkLines | Where-Object { $_.StartsWith('PASS') }).Count
    $totalCount = $checkLines.Count
    Write-Host ""
    Write-Host "Dry run log: $logPath"
    Write-Host "Results: $passCount/$totalCount PASS"

    if ($KeepEvidence) {
        Write-Host "Evidence kept at: $scratch"
    }
    else {
        try { Remove-Item -Path $scratch -Recurse -Force -ErrorAction Stop } catch { Write-Host "Warning: could not clean up '$scratch': $($_.Exception.Message)" }
    }
}

exit $exitCode
