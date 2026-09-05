# M8 — Roster as data

**Goal:** Participants become rows in the database, named for models, carrying host and model, and every place that today hard-codes `owner`/`claude`/`codex` reads the roster instead.

**Architecture:** The `participants` table (schema v3) becomes the single source of participant identity: `TokenStore` mints one token per roster id, `--print-config` emits a host file per app-backed row, the MCP instructions and tool descriptions are generated from the roster at hub start, `list_rooms` and a new `GET /api/participants` return it, and the web client renders names, badges, mentions and "mine" styling from the fetched roster. Existing ids `owner`, `claude`, `codex` are kept verbatim (see "Decisions taken here"); nine spawn rows are seeded and are inert until M5 gives them a spawner.

**Author model:** Claude Fable 5.1 (session model; tier routing satisfied — HIGH plans on Fable).

**Blast radius:** HIGH. A schema migration on the owner's live database; a change to the on-disk `tokens.json` shape (more keys) that every pasted host config depends on; a change to the MCP instructions every host reads. `references/verification-tiers.md`: schema-evolution guard tests + synthetic-corpus dry run + two critic passes.

Written for builder-subagent execution; if something doesn't match, STOP and report rather than guess.

Size: ~80 KB after folding critique pass 1, over the 60 KB WARN line because every test is written in full rather than described and the dry-run rehearsal is specified to the line. The milestone is one seam (participant identity) and would not split cleanly; the size is accepted, not ignored.

Binding definition: `docs/superpowers/plans/grill-notes-m5-autonomy.md` — D14 (roster), F7 (Codex models), F8 (Claude aliases; `fable` flagged). Lessons consulted: M1 `[sqlite, schema, migrations]` (stamp last, in the same transaction; probe before ALTER), M2 `[sqlite, wal, testing, migrations]` (raw-SQL fixtures, `ClearAllPools`, WAL three-file rule), M3 `[msbuild, node, csproj]` (client build is part of `dotnet build`; `npm ci` once).

## Decisions taken here (B-class: reversible rulings, logged not asked)

1. **Existing ids stay `owner`, `claude`, `codex`.** D14's example ids (`claude-desktop`, `codex-app`) would mean rewriting `messages.author_id` and `read_cursors.participant_id` on the live database and invalidating both pasted host configs, for no behaviour the owner asked for. The ruling that matters ("Claude Code labeled as claude", 2026-09-04) is honoured by keeping `claude`. Rename later if wanted: a v4 migration, not this row.
2. **Spawn rows are seeded now, inert until M5.** Ids equal the model name the host takes on the command line (`opus`, `sonnet`, `fable`; `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, `gpt-5.4-mini`), so M5's spawner reads `model` straight off the row. `haiku`, `opus[1m]`, `sonnet[1m]`, `opusplan`, `best`, `default` are NOT seeded: the owner named "all of sonnet/opus/fable" and Codex's list; the bracketed aliases are not valid ids in a mention regex anyway.
3. **Runtime Codex discovery (F7 `model/list`) is M5's,** where process spawning lives. M8 seeds the list Codex verified on 2026-09-05. A roster row is data, so adding a model later is one `INSERT`, no code.
4. **No `enabled` column.** Nothing in M8 reads it; whether a spawn row may be spawned is spawner config (M5). An unreferenced row is deletable by hand; a row with a message or cursor is protected by the FKs.
5. **Tokens and instructions are startup-static,** like `TokenStore`: the hub reads the roster once in `HubHost.Build` for those two. `list_rooms`, `GET /api/participants` and the human-row lookup on a web post query the table live. So a row inserted while the hub runs is visible in the client and in `list_rooms` at once, but has no token and no mention line until the next start. Documented in the README the hub writes ("a new row gets its token and its mention line at the next hub start").
6. **Non-serving verbs never migrate.** `--print-config` and `--rotate-token` read the roster from the database only when it is already at v3; otherwise they exit 4 with "start the hub once". This keeps the MINOR-12 property (a read-only verb has no write side effect) for the schema as well as for tokens.
7. **Accent colour is per host family — provisional.** `opus` renders in the Claude accent, `gpt-6-astra` in the Codex accent; name and badge distinguish rows, on message rows AND on `@mention` chips (both are keyed by host after this plan). Revisit after the first session with two Claude-family rows talking; it is CSS, so the revert is cheap.

## Acceptance

- A1 WHEN the hub starts against a v2 database THE SYSTEM SHALL write a verified `chopitup.db.v2.<stamp>.bak` beside it, stamp the database v3, keep every message, cursor and room byte-for-byte in meaning, set `host` on the three existing rows, and seed the nine spawn rows; a second start SHALL change nothing.
- A2 WHEN the hub starts against a `tokens.json` holding the three v2 keys THE SYSTEM SHALL leave those three values byte-identical and add one token per new roster id, and `TokenStore.TryResolve` SHALL resolve every roster id from its token.
- A3 WHEN `--print-config` runs THE SYSTEM SHALL write exactly `claude-desktop.json` (token of the app-backed Claude row), `codex-config.toml` (token of the app-backed Codex row) and `README.md`, and the README SHALL list every roster row with its host and model, marking spawn rows as having no file and `fable` with its usage-credit note.
- A4 WHEN any MCP client initialises THE SYSTEM SHALL send instructions that name every roster id in the "Address" line, and `list_rooms` SHALL return a `participants` array with `id`, `display_name`, `kind`, `host`, `model` for every row.
- A5 WHEN `--rotate-token <id>` runs for any roster id THE SYSTEM SHALL mint only that token; for an id not in the roster it SHALL exit 2 naming every roster id.
- A6 WHEN the web client loads THE SYSTEM SHALL fetch `GET /api/participants` and render author names, two-letter badges, `@mention` decoration and the "mine" style from the fetched roster, with no participant id typed into client source.
- A7 WHEN the repository is searched THE SYSTEM SHALL contain no `TokenStore.Participants` constant and no participant-id string literal outside `ChopDb` seeds, tests and the corpus tool; host-name literals (`"claude"`, `"codex"` as the program that speaks for a row) are allowed only in `HostConfigs.cs` and `participants.ts`.
- A8 WHEN `--print-config` or `--rotate-token` runs against a data directory whose database is below v3 or absent THE SYSTEM SHALL write nothing and exit 4 telling the owner to start the hub once.

## Claim ledger

| # | Claim | Verified at (commit) | Recheck (pwsh, exit 0 = holds) |
|---|-------|----------------------|--------------------------------|
| 1 | Baseline: 111 tests green (33 Core + 78 Hub) | f02c413 | `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal 2>&1 \| Select-String 'Passed:\s+(33\|78),' \| Measure-Object \| % { if ($_.Count -eq 2) { exit 0 } else { exit 1 } }` |
| 2 | `ChopDb.LatestSchemaVersion = 2` at line 9; migration driver lines 73–74 are `if (version < 1) ApplyV1(conn); if (GetUserVersion(conn) < 2) ApplyV2(conn);` | f02c413 | `Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern 'LatestSchemaVersion = 2;' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 3 | `participants` has exactly `id, display_name, kind` (ApplyV1 lines 207–211; fixture `SchemaMigrationTests.WriteRawV1`) | f02c413 | `Select-String -Path src/ChopItUp.Core/Storage/ChopDb.cs -Pattern "kind\s+TEXT NOT NULL" -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 4 | `TokenStore.Participants` is `public static readonly string[]` at line 13 and is referenced from exactly one place outside the class: `tests/ChopItUp.Hub.Tests/HostCommandsTests.cs:63` | f02c413 | `$n = (Get-ChildItem src,tests -Recurse -Include *.cs \| Select-String -Pattern 'TokenStore\.Participants').Count; if ($n -eq 1) { exit 0 } else { exit 1 }` |
| 5 | `HostConfigs.Write(string dataDir, int port, IReadOnlyDictionary<string,string> tokens)` indexes `tokens["claude"]` and `tokens["codex"]` (lines 23–24) | f02c413 | `Select-String -Path src/ChopItUp.Hub/Hosting/HostConfigs.cs -Pattern 'tokens\["claude"\]' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 6 | `Participation.Instructions` is a `const string` and `HubHost.Build` assigns it to `o.ServerInstructions` | f02c413 | `Select-String -Path src/ChopItUp.Hub/Hosting/HubHost.cs -Pattern 'ServerInstructions = Participation\.Instructions' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 7 | `ChatApi` posts as the literal `"owner"` at exactly two sites (lines 53, 72) | f02c413 | `$n = (Select-String -Path src/ChopItUp.Hub/Web/ChatApi.cs -Pattern 'store\.Post\(roomId, "owner"').Count; if ($n -eq 2) { exit 0 } else { exit 1 }` |
| 8 | Client: `participants.ts` exports `MENTIONABLE`, `displayName`, `badgeFor`, `accentClass`; imported only by `markdown.ts` and `Thread.tsx`; `Thread.tsx:70` computes `mine` from the literal `'owner'` | f02c413 | `$n = (Get-ChildItem src/ChopItUp.Hub/client/src -Include *.ts,*.tsx -Recurse \| Select-String -Pattern "from './participants'").Count; if ($n -eq 2) { exit 0 } else { exit 1 }` |
| 9 | `ParticipationTests.A1` asserts the literal `"@owner, @claude or @codex"`; `ChopDbTests` line 19 asserts seeded ids `claude,codex,owner`; `HostCommandsTests.A7` asserts three files | f02c413 | `Select-String -Path tests/ChopItUp.Hub.Tests/ParticipationTests.cs -Pattern '@owner, @claude or @codex' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 10 | `tools/Invoke-M2DryRun.ps1` asserts `/health` schema `-eq 2` (line 133), backup stamped v1 (line 150), and the three config files (line 225); corpus tool writes v1 (`CorpusBuilder.cs:148`) | f02c413 | `Select-String -Path tools/Invoke-M2DryRun.ps1 -Pattern 'health.schema.*-eq 2' -Quiet \| % { if ($_) { exit 0 } else { exit 1 } }` |
| 11 | `tools/Invoke-M4SelfCheck.ps1` reads `$tokens.claude` from `tokens.json` (lines 331, 416) and nothing else about the roster | f02c413 | `$n = (Select-String -Path tools/Invoke-M4SelfCheck.ps1 -Pattern '\$tokens2?\.claude').Count; if ($n -eq 2) { exit 0 } else { exit 1 }` |
| 12 | Codex model list `gpt-6-astra, gpt-5.6-sol, gpt-5.6-terra, gpt-5.6-luna, gpt-5.5, gpt-5.4-mini` | Codex, room `general` message 11, 2026-09-05 | — |
| 13 | The MCP SDK's `[Description]` on tool parameters must be a compile-time constant (attribute argument) | C# language rule | — |

## Task table

| # | Task | Builder | Files |
|---|------|---------|-------|
| 1 | Schema v3 + `Participant` model + `ParticipantStore` + guard tests | sonnet | `ChopDb.cs`, `Model/Message.cs`, new `Storage/ParticipantStore.cs`, `SchemaMigrationTests.cs`, `ChopDbTests.cs`, new `ParticipantStoreTests.cs` |
| 2 | `TokenStore` minted from the roster | sonnet | `TokenStore.cs`, `HubHost.cs`, `HostCommands.cs` (stub), `HubTestHost.cs`, `HostCommandsTests.cs`, new `TokenStoreTests.cs` |
| 3 | Non-serving verbs read the roster; `HostConfigs` per app-backed row; README roster table | sonnet | `HostCommands.cs`, `HostConfigs.cs`, `HostCommandsTests.cs` |
| 4 | Instructions + tool prose generated from the roster; `list_rooms` returns it | sonnet | `Participation.cs`, `RoomTools.cs`, `HubHost.cs`, `ParticipationTests.cs`, `RoomToolsTests.cs` |
| 5 | `ChatApi` posts as the human row; `GET /api/participants` | sonnet | `ChatApi.cs`, `ChatApiTests.cs` |
| 6 | Dry run rehearses the real upgrade (v2 corpus, pre-existing tokens); self-check follows v3 | sonnet | `tools/ChopItUp.Corpus/CorpusBuilder.cs`, `tools/ChopItUp.Corpus/Program.cs`, `tools/Invoke-M2DryRun.ps1`, `tools/Invoke-M4SelfCheck.ps1` |
| 7 | Client renders the fetched roster | opus | `client/src/participants.ts`, `markdown.ts`, `Thread.tsx`, `App.tsx`, `api.ts`, `types.ts`, `styles.css` |

Edges: 1→2→3; 2→4; 2→5; 6 needs 3 and 4 (it asserts config files and `/health`); 7 needs 5. Dispatch sequentially in numeric order.

Line-ending rule for every task: the repo stores LF, the working tree is CRLF (`core.autocrlf=true`, no `.gitattributes`). Edit with tools that preserve the file's existing line endings; if a diff shows whole-file churn, normalise before `git add`.

Gate rule for every task: after each `dotnet test`, read the `Passed!`/`Failed!` summary line AND `$LASTEXITCODE` (must be 0); quote both in the report. "Expect N passed" below is the count after the reconciliation bullets in that task — the reconciliations are part of the task, not optional.

---

## Task 1 — Schema v3, `Participant`, `ParticipantStore`

**RED first.** Add to `tests/ChopItUp.Core.Tests/Storage/SchemaMigrationTests.cs` a raw-SQL v2 fixture and two tests; they fail to compile until `ParticipantStore` and `ChopDb.SeedRoster` exist, then fail on `Assert.Equal(3, ...)` until `ApplyV3` exists. Run `dotnet test tests/ChopItUp.Core.Tests -c Debug --nologo -v minimal --filter "FullyQualifiedName~SchemaMigrationTests"` and show the RED output before touching `src/`.

```csharp
    private void WriteRawV2()
    {
        // v1 shape plus exactly what ApplyV2 adds. Raw SQL on purpose (LESSONS M2): this must keep
        // describing v2 after ChopDb can no longer produce one.
        Directory.CreateDirectory(_dir);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL,
                client_key TEXT);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            INSERT INTO participants (id, display_name, kind) VALUES ('owner','Owner','human'),('claude','Claude','model'),('codex','Codex','model');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at, client_key) VALUES
                (1,'general','owner','first v2 message','2026-09-01T10:01:00.000+00:00',NULL),
                (2,'general','codex','second v2 message','2026-09-01T10:02:00.000+00:00','k-1');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('claude','general',2);
            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void M8_A1_v2_database_is_backed_up_then_migrated_to_v3_with_the_roster_seeded()
    {
        WriteRawV2();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(3, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Contains(".v2.", Path.GetFileName(db.LastBackupPath!));

        var roster = new ParticipantStore(db).List();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), roster.Select(p => p.Id));
        var owner = roster.Single(p => p.Id == "owner");
        Assert.Equal(("human", "human", (string?)null), (owner.Kind, owner.Host, owner.Model));
        var claude = roster.Single(p => p.Id == "claude");
        Assert.Equal(("model", "claude", (string?)null), (claude.Kind, claude.Host, claude.Model));
        var fable = roster.Single(p => p.Id == "fable");
        Assert.Equal(("model", "claude", "fable"), (fable.Kind, fable.Host, fable.Model));
        Assert.Contains("usage credits", fable.Note);
        var astra = roster.Single(p => p.Id == "gpt-6-astra");
        Assert.Equal(("model", "codex", "gpt-6-astra"), (astra.Kind, astra.Host, astra.Model));

        // Every v2 meaning survives: rows, the retry key, the cursor.
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT client_key FROM messages WHERE id = 2";
        Assert.Equal("k-1", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT last_read_id FROM read_cursors WHERE participant_id = 'claude' AND room_id = 'general'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);

        // Idempotent: a second EnsureDatabase migrates nothing and backs up nothing.
        db.EnsureDatabase();
        Assert.Null(db.LastBackupPath);
        Assert.Equal(3, db.GetSchemaVersion());
    }

    [Fact]
    public void M8_A1_torn_v3_with_a_column_present_but_stamp_2_is_repaired_not_crashed()
    {
        WriteRawV2();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE participants ADD COLUMN host TEXT;";   // half of v3 landed, stamp did not
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(3, db.GetSchemaVersion());
        Assert.Equal("claude", new ParticipantStore(db).List().Single(p => p.Id == "claude").Host);
    }
```

Sweep every `Assert.Equal(2, db.GetSchemaVersion());` to `Assert.Equal(ChopDb.LatestSchemaVersion, db.GetSchemaVersion());`. At HEAD there are seven: `SchemaMigrationTests.cs` lines 55, 127, 139, 159, 207, 228 and `tests/ChopItUp.Hub.Tests/DryRunTests.cs:30`. Also in this task, `tests/ChopItUp.Hub.Tests/HubHostTests.cs:23-24`: `Assert.Equal(3, tokens.Count);` and `Assert.Equal(3, tokens.Tokens.Values.Distinct().Count());` → `ChopDb.SeedRoster.Count` in both (+ `using ChopItUp.Core.Storage;`). Those two Hub sites are the only Hub tests the seed change touches; sweeping them here keeps the Hub suite green at this task's commit (`TokenStore.Load` still mints for its three-name constant, so the hub starts; the count assertions are what would go red). Backup assertions on `.v1.` stay true: the backup is taken once, from v1. A test name that says "v2" (e.g. `A8_fresh_directory_lands_on_v2…`) may be renamed to say "the latest version"; not required.

Reconcile the three-row assumptions that the seed change breaks (they are the count of `participants`, which goes 3 → 12): `SchemaMigrationTests.cs:86` `Assert.Equal(3L, ScalarLong("SELECT COUNT(*) FROM participants"));` and `ChopDbTests.cs:32` and `:50` `Assert.Equal(3L, Scalar<long>("SELECT COUNT(*) FROM participants"));` each become `Assert.Equal((long)ChopDb.SeedRoster.Count, ...)` with the same query. The comment on line 86 ("seeds not duplicated by the ladder") stays true and stays.

Update `tests/ChopItUp.Core.Tests/Storage/ChopDbTests.cs` line 19 to assert the seeded roster from the same source of truth:

```csharp
        Assert.Equal(
            ChopDb.SeedRoster.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            Scalar<string>("SELECT group_concat(id, ',') FROM (SELECT id FROM participants ORDER BY id)").Split(','));
```

**GREEN.** `src/ChopItUp.Core/Model/Message.cs` — append:

```csharp
/// <summary>One roster row. <see cref="Host"/> is which program speaks for this row: <c>human</c>,
/// <c>claude</c> (Claude Desktop / Claude Code) or <c>codex</c>. <see cref="Model"/> is null for the
/// human and for the app-backed rows (whatever model the app has selected), and the model name the
/// host takes on its command line for a spawn row (M5). <see cref="Note"/> is owner-facing text
/// shown beside the row in the generated README, e.g. the usage-credit warning on <c>fable</c>.</summary>
public sealed record Participant(string Id, string DisplayName, string Kind, string Host, string? Model, string? Note);
```

`src/ChopItUp.Core/Storage/ChopDb.cs`:

- line 9: `public const int LatestSchemaVersion = 3;`
- after that line add the seed roster (the one list every seed, test and README derives from):

```csharp
    /// <summary>The roster a fresh database starts with, in display order. Ids of spawn rows are the
    /// model names their host accepts on the command line, so M5 reads <c>Model</c> straight off the
    /// row (grill D14, F7, F8). Existing ids owner/claude/codex are kept: renaming them would rewrite
    /// every message's author on the live database for nothing the owner asked for.</summary>
    public static readonly IReadOnlyList<Participant> SeedRoster =
    [
        new("owner",         "Owner",         "human", "human",  null,            null),
        new("claude",        "Claude",        "model", "claude", null,            "App-backed: Claude Desktop or Claude Code, whatever model the app has selected."),
        new("codex",         "Codex",         "model", "codex",  null,            "App-backed: the Codex app or CLI, whatever model the app has selected."),
        new("opus",          "Opus",          "model", "claude", "opus",          null),
        new("sonnet",        "Sonnet",        "model", "claude", "sonnet",        null),
        new("fable",         "Fable",         "model", "claude", "fable",         "May bill to usage credits instead of the plan's included limits."),
        new("gpt-6-astra",   "GPT-6 Astra",   "model", "codex",  "gpt-6-astra",   null),
        new("gpt-5.6-sol",   "GPT-5.6 Sol",   "model", "codex",  "gpt-5.6-sol",   null),
        new("gpt-5.6-terra", "GPT-5.6 Terra", "model", "codex",  "gpt-5.6-terra", null),
        new("gpt-5.6-luna",  "GPT-5.6 Luna",  "model", "codex",  "gpt-5.6-luna",  null),
        new("gpt-5.5",       "GPT-5.5",       "model", "codex",  "gpt-5.5",       null),
        new("gpt-5.4-mini",  "GPT-5.4 Mini",  "model", "codex",  "gpt-5.4-mini",  null),
    ];
```

  (`using ChopItUp.Core.Model;` at the top of the file.)

- driver, lines 73–74 become:

```csharp
            if (version < 1) ApplyV1(conn);
            if (GetUserVersion(conn) < 2) ApplyV2(conn);
            if (GetUserVersion(conn) < 3) ApplyV3(conn);
```

- append after `ApplyV2`:

```csharp
    /// <summary>v3 makes the roster data (M8): <c>host</c>, <c>model</c> and <c>note</c> on
    /// participants, the three original rows told which host they are, and the spawn rows seeded.
    /// Each column is probed before its ALTER (SQLite has no ADD COLUMN IF NOT EXISTS) so a torn v3
    /// is re-runnable; INSERT OR IGNORE keeps a hand-edited roster; the stamp is the last statement
    /// of the same transaction (LESSONS, M1).</summary>
    private static void ApplyV3(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        var ddl = new System.Text.StringBuilder();
        foreach (var column in new[] { "host", "model", "note" })
        {
            using var probe = conn.CreateCommand();
            probe.Transaction = tx;
            probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('participants') WHERE name = '{column}'";
            if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
                ddl.Append($"ALTER TABLE participants ADD COLUMN {column} TEXT;\n");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = ddl + """
                UPDATE participants SET host = 'human'  WHERE id = 'owner'  AND host IS NULL;
                UPDATE participants SET host = 'claude' WHERE id = 'claude' AND host IS NULL;
                UPDATE participants SET host = 'codex'  WHERE id = 'codex'  AND host IS NULL;
                """;
            cmd.ExecuteNonQuery();
        }
        SeedParticipants(conn, tx);   // OR IGNORE: existing rows keep their display_name
        BackfillNotes(conn, tx);      // the three original rows never had a note column to keep
        using (var stamp = conn.CreateCommand())
        {
            stamp.Transaction = tx;
            stamp.CommandText = "PRAGMA user_version = 3;";
            stamp.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void SeedParticipants(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO participants (id, display_name, kind, host, model, note) VALUES ($id, $name, $kind, $host, $model, $note)";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var name = cmd.Parameters.Add("$name", SqliteType.Text);
        var kind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var host = cmd.Parameters.Add("$host", SqliteType.Text);
        var model = cmd.Parameters.Add("$model", SqliteType.Text);
        var note = cmd.Parameters.Add("$note", SqliteType.Text);
        foreach (var p in SeedRoster)
        {
            id.Value = p.Id; name.Value = p.DisplayName; kind.Value = p.Kind; host.Value = p.Host;
            model.Value = (object?)p.Model ?? DBNull.Value; note.Value = (object?)p.Note ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Rows that pre-date v3 (seeded by V1, skipped by the OR IGNORE above) get the seed's
    /// note if they have none. Only NULL is filled: a note the owner wrote by hand is never replaced.</summary>
    private static void BackfillNotes(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE participants SET note = $note WHERE id = $id AND note IS NULL";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var note = cmd.Parameters.Add("$note", SqliteType.Text);
        foreach (var p in SeedRoster.Where(p => p.Note is not null))
        {
            id.Value = p.Id; note.Value = p.Note;
            cmd.ExecuteNonQuery();
        }
    }
```

  `ApplyV1` is left as is: a fresh database runs V1 → V2 → V3 in one `EnsureDatabase`, and V3 fills in what V1's three-row seed lacks. Do not add the new columns to V1's CREATE TABLE — the v1 fixture must keep describing v1, and V3 must keep working on it.

  Net effect on a v2 database AND on a fresh one (which runs V1 → V2 → V3 in one call, so its three original rows also arrive at V3 without a note): the three original rows keep their `display_name` (owner may have edited it), gain `host`, and get the seed note where they had none. That is what makes `Assert.Equal(ChopDb.SeedRoster, store.List())` hold on a fresh database. The `host` column is nullable in DDL (ALTER cannot add NOT NULL without a default) but every row has one after V3; `ParticipantStore` reads it with `GetString` and would throw on a NULL, which is the right failure for a hand-inserted row missing its host.

New file `src/ChopItUp.Core/Storage/ParticipantStore.cs`:

```csharp
using ChopItUp.Core.Model;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

/// <summary>Reads the roster. Rows come back in insertion order (rowid), which is seed order for a
/// fresh database and "original three, then whatever was added" for a migrated one.</summary>
public sealed class ParticipantStore(ChopDb db)
{
    public IReadOnlyList<Participant> List()
    {
        using var conn = db.Open();
        return ReadAll(conn);
    }

    /// <summary>The read itself, on any open connection — including the pragma-free one the
    /// non-serving verbs open (they must not go through <see cref="ChopDb.Open"/>, whose WAL pragma
    /// rewrites a non-WAL file's header).</summary>
    public static IReadOnlyList<Participant> ReadAll(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, kind, host, model, note FROM participants ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var rows = new List<Participant>();
        while (reader.Read())
            rows.Add(new Participant(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    /// <summary>The one human. The schema allows more than one row of kind 'human'; the hub does not
    /// (grill: "the owner is the only human here"), so this throws rather than picking one.</summary>
    public string HumanId()
    {
        var humans = List().Where(p => p.Kind == "human").Select(p => p.Id).ToArray();
        return humans.Length == 1
            ? humans[0]
            : throw new InvalidOperationException($"Expected exactly one participant of kind 'human', found {humans.Length}.");
    }
}
```

New file `tests/ChopItUp.Core.Tests/Storage/ParticipantStoreTests.cs`:

```csharp
using ChopItUp.Core.Storage;

namespace ChopItUp.Core.Tests.Storage;

public sealed class ParticipantStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_roster_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void List_returns_the_seed_roster_in_seed_order_and_HumanId_is_owner()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        var store = new ParticipantStore(db);

        Assert.Equal(ChopDb.SeedRoster, store.List());
        Assert.Equal("owner", store.HumanId());
    }

    [Fact]
    public void HumanId_throws_when_the_roster_has_two_humans()
    {
        var db = new ChopDb(Path.Combine(_dir, "chopitup.db"));
        db.EnsureDatabase();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO participants (id, display_name, kind, host) VALUES ('guest','Guest','human','human')";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new ParticipantStore(db).HumanId());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

`Assert.Equal(ChopDb.SeedRoster, store.List())` relies on record equality; the migrated-database case is covered by the schema test, which compares ids only (notes on the original rows differ there by design).

Run the whole solution: `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` (0 warnings) then `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal` — expect Core 33 + 4 = 37 passed and Hub 78 passed (unchanged count; two assertions reconciled).

Commit: `M8 task 1: schema v3 makes the roster data`.

---

## Task 2 — `TokenStore` minted from the roster

**RED first.** New `tests/ChopItUp.Hub.Tests/TokenStoreTests.cs`:

```csharp
using System.Text.Json;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Security;

namespace ChopItUp.Hub.Tests;

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_tok_" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Roster = ChopDb.SeedRoster.Select(p => p.Id).ToArray();

    [Fact]
    public void M8_A2_a_three_key_tokens_json_is_backfilled_with_the_original_values_byte_identical()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, TokenStore.FileName);
        var original = new Dictionary<string, string> { ["owner"] = "tok-owner", ["claude"] = "tok-claude", ["codex"] = "tok-codex" };
        File.WriteAllText(path, JsonSerializer.Serialize(original));

        var store = TokenStore.Load(_dir, Roster);

        Assert.Equal(Roster.Length, store.Count);
        foreach (var (id, token) in original) Assert.Equal(token, store.Tokens[id]);
        foreach (var id in Roster) Assert.False(string.IsNullOrWhiteSpace(store.Tokens[id]));
        foreach (var (id, token) in store.Tokens)
        {
            Assert.True(store.TryResolve(token, out var resolved));
            Assert.Equal(id, resolved);
        }
        var reread = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        Assert.Equal(store.Tokens.OrderBy(kv => kv.Key), reread.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void M8_A5_rotate_accepts_any_roster_id_and_rejects_others_naming_the_roster()
    {
        TokenStore.Load(_dir, Roster);
        var before = TokenStore.ReadExisting(_dir, Roster);

        var minted = TokenStore.Rotate(_dir, Roster, "gpt-6-astra");
        var after = TokenStore.ReadExisting(_dir, Roster);
        Assert.Equal(minted, after["gpt-6-astra"]);
        foreach (var id in Roster.Where(id => id != "gpt-6-astra")) Assert.Equal(before[id], after[id]);

        var ex = Assert.Throws<ArgumentException>(() => TokenStore.Rotate(_dir, Roster, "mallory"));
        foreach (var id in Roster) Assert.Contains(id, ex.Message);
    }

    [Fact]
    public void ReadExisting_names_every_roster_id_missing_from_the_file()
    {
        TokenStore.Load(_dir, ["owner", "claude"]);
        var ex = Assert.Throws<InvalidOperationException>(() => TokenStore.ReadExisting(_dir, Roster));
        Assert.Contains("gpt-5.4-mini", ex.Message);
        Assert.DoesNotContain("owner", ex.Message.Split(':').Last());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

Compile fails (no such overloads). Show it.

**GREEN.** `src/ChopItUp.Hub/Security/TokenStore.cs`:

- delete line 13 (`public static readonly string[] Participants = ...`).
- `Load(string dataDir)` → `Load(string dataDir, IReadOnlyList<string> participantIds)`; in the body `foreach (var p in Participants)` → `foreach (var p in participantIds)`. Add to the doc comment: "The ids come from the roster the hub read at start (`ParticipantStore.List`): a roster row with no token gets one minted here, which is how a database upgraded to v3 gets its nine new tokens on the next start." The per-id stderr line (`tokens.json had no token for '{p}'; minted a new one. Paste it into that host's config.`) becomes `tokens.json had no token for '{p}'; minted one. If that participant has a host file, run --print-config and re-paste it.` — on the owner's upgrade start this line prints nine times, for rows that have no host file, so the old wording would be nine false instructions.
- `ReadExisting(string dataDir)` → `ReadExisting(string dataDir, IReadOnlyList<string> participantIds)`; `Participants.Where(...)` → `participantIds.Where(...)`.
- `Rotate(string dataDir, string participant)` → `Rotate(string dataDir, IReadOnlyList<string> participantIds, string participant)`; the guard becomes `if (!participantIds.Contains(participant, StringComparer.Ordinal)) throw new ArgumentException($"Unknown participant '{participant}'. Known: {string.Join(", ", participantIds)}.", nameof(participant));`.

`src/ChopItUp.Hub/Hosting/HubHost.cs` lines 44–49 (from `var db = new ChopDb(` through `AddSingleton(new MessageStore(db));`) become:

```csharp
            var db = new ChopDb(Path.Combine(options.DataDir, "chopitup.db"));
            db.EnsureDatabase();
            var participants = new ParticipantStore(db);
            // Startup-static, like the tokens: the roster is read once here, and every consumer
            // below (tokens, instructions, tools) sees the same list. Editing rows takes effect at
            // the next hub start.
            var roster = participants.List();
            var tokens = TokenStore.Load(options.DataDir, roster.Select(p => p.Id).ToArray());

            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(new MessageStore(db));
            builder.Services.AddSingleton(participants);
```

  Line 50 `builder.Services.AddSingleton<MessageSignal>();` and line 51 `builder.Services.AddSingleton(tokens);` stay exactly where they are, after the block. Task 4 will change the `ServerInstructions` line; leave it for now.

Reconcile `tests/ChopItUp.Hub.Tests/HubHostTests.cs:22`: `TokenStore.Load(_dir)` → `TokenStore.Load(_dir, ChopDb.SeedRoster.Select(p => p.Id).ToArray())` (lines 23–24 were already reconciled in Task 1). That is every `TokenStore.Load(` call site in the solution: `HubHost.cs:46`, `HubTestHost.cs:32`, `HubHostTests.cs:22`, and the ten in `HostCommandsTests.cs`.

`tests/ChopItUp.Hub.Tests/HubTestHost.cs` line 32: `Tokens = TokenStore.Load(dir);` → `Tokens = TokenStore.Load(dir, ChopDb.SeedRoster.Select(p => p.Id).ToArray());` with `using ChopItUp.Core.Storage;`. (The hub has already minted every token by the time the constructor runs, so this `Load` mints nothing; passing the seed ids keeps the test host honest if the seed grows.)

`tests/ChopItUp.Hub.Tests/HostCommandsTests.cs`: every `TokenStore.Load(dir)` → `TokenStore.Load(dir, Roster)` where the class gains `private static readonly string[] Roster = ChopDb.SeedRoster.Select(p => p.Id).ToArray();` (+ `using ChopItUp.Core.Storage;`). Line 63 `foreach (var p in TokenStore.Participants)` → `foreach (var p in Roster)`.

`HostCommands.cs` calls the old overloads and would not compile. Task 3 owns that file; to keep this commit green, make only the two call-site changes now: `TokenStore.Rotate(options.DataDir, options.RotateParticipant!)` → `TokenStore.Rotate(options.DataDir, RosterIds(), options.RotateParticipant!)` and `TokenStore.ReadExisting(options.DataDir)` → `TokenStore.ReadExisting(options.DataDir, RosterIds())`, with this stub that Task 3 replaces:

```csharp
    private static IReadOnlyList<string> RosterIds() =>
        ChopDb.SeedRoster.Select(p => p.Id).ToArray();   // Task 3 reads the database instead
```

  (`using ChopItUp.Core.Storage;`.)

Run `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` (0 warnings) and `dotnet test ChopItUp.slnx -c Debug --nologo -v minimal`. Expected: Hub tests 78 + 3 = 81 passed; `HostCommandsTests.A7` still passes (three files) because `HostConfigs` still indexes `claude`/`codex`, which exist. `HostCommandsTests.A6` passes because the error message now lists the seed roster and the test loops the same list.

Commit: `M8 task 2: tokens minted per roster id`.

---

## Task 3 — Non-serving verbs read the roster; `HostConfigs` per app-backed row

**RED first.** In `tests/ChopItUp.Hub.Tests/HostCommandsTests.cs`:

- Every test that runs `--print-config` or `--rotate-token` and expects success now needs a v3 database beside the tokens, because the verbs read the roster from it. Add a helper and call it in those tests right after `NewDir()`:

```csharp
    /// <summary>What "start the hub once" leaves behind: a v3 database and a full tokens.json.</summary>
    private static void StartedOnce(string dir)
    {
        new ChopDb(Path.Combine(dir, "chopitup.db")).EnsureDatabase();
        TokenStore.Load(dir, Roster);
    }
```

  Rule: every test that runs `PrintConfig` or `RotateToken` and expects exit 0 (or expects a rotation to happen) needs `StartedOnce(dir)` where it currently has its first `TokenStore.Load(dir, Roster)`. At HEAD those sites are lines 33 (`var before = TokenStore.Load(dir).Tokens.ToDictionary(...)` — keep the assignment, call `StartedOnce(dir)` on the line before and read `before` from `TokenStore.ReadExisting(dir, Roster)`), 54, 151, 221, 244, 259, 308 (same pattern where it is an assignment). Lines 40 and 107 are post-condition reads and stay `Load`. Tests A6b (line 67) and `A7_print_config_against_a_directory_with_no_tokens_file_creates_nothing` (272) keep NOT calling it. `A7_print_config_does_not_mint_a_missing_token` (284) writes its own partial tokens.json — give it a v3 database too (`new ChopDb(Path.Combine(dir, "chopitup.db")).EnsureDatabase();`) so it fails on the missing token, not on the missing database.

- Add:

```csharp
    [Fact]
    public void M8_A8_print_config_against_a_v2_database_writes_nothing_and_says_start_the_hub()
    {
        var dir = NewDir();
        TokenStore.Load(dir, ["owner", "claude", "codex"]);
        WriteRawV2(Path.Combine(dir, "chopitup.db"));   // what the previous build left behind
        var names = Snapshot(dir);
        var dbBytes = File.ReadAllBytes(Path.Combine(dir, "chopitup.db"));   // names alone cannot see a header rewrite

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains("start the hub once", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(names, Snapshot(dir));
        Assert.Equal(dbBytes, File.ReadAllBytes(Path.Combine(dir, "chopitup.db")));
    }

    [Fact]
    public void M8_A8_print_config_against_a_newer_database_exits_4_without_templating_it()
    {
        var dir = NewDir();
        StartedOnce(dir);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "chopitup.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains("newer build", error.ToString());
        Assert.False(Directory.Exists(ConfigFolder(dir)));
    }

    [Fact]
    public void M8_A8_rotate_against_a_missing_database_writes_nothing_and_exits_4()
    {
        var dir = NewDir();
        TokenStore.Load(dir, Roster);
        var before = File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName));

        var error = new StringWriter();
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "opus"), new StringWriter(), error);

        Assert.Equal(4, exit);
        Assert.Contains("start the hub once", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, TokenStore.FileName)));
    }

    [Fact]
    public void M8_A3_A5_print_config_uses_the_app_backed_rows_and_lists_the_whole_roster()
    {
        var dir = NewDir();
        StartedOnce(dir);
        var tokens = TokenStore.ReadExisting(dir, Roster);

        Assert.Equal(0, HostCommands.Run(new HubOptions(dir, Port: 9123, HubCommand.PrintConfig), new StringWriter(), new StringWriter()));

        var folder = ConfigFolder(dir);
        var claude = File.ReadAllText(Path.Combine(folder, "claude-desktop.json"));
        var codex = File.ReadAllText(Path.Combine(folder, "codex-config.toml"));
        var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
        Assert.Contains(tokens["claude"], claude);
        Assert.Contains(tokens["codex"], codex);
        foreach (var p in ChopDb.SeedRoster)
        {
            Assert.Contains($"`{p.Id}`", readme);
            Assert.DoesNotContain(tokens[p.Id], readme);   // the README never carries a token
        }
        Assert.Contains("usage credits", readme);
        Assert.Contains("no file", readme);

        // Rotating a spawn row's token changes only that key.
        var exit = HostCommands.Run(new HubOptions(dir, Port: 0, HubCommand.RotateToken, "gpt-5.5"), new StringWriter(), new StringWriter());
        Assert.Equal(0, exit);
        var after = TokenStore.ReadExisting(dir, Roster);
        Assert.NotEqual(tokens["gpt-5.5"], after["gpt-5.5"]);
        foreach (var id in Roster.Where(id => id != "gpt-5.5")) Assert.Equal(tokens[id], after[id]);
    }
```

  `WriteRawV2(string path)` is the v2 fixture from Task 1 with the path parameterised — copy it into this test class (a Hub test cannot reach a private Core test helper; duplication is deliberate, the fixture must stay raw SQL in both places). Include `using Microsoft.Data.Sqlite;`.

Also reconcile `A6b_rotate_against_a_directory_with_no_tokens_file_creates_nothing` (line 74 asserts the error names `tokens.json`): it keeps passing only because `RotateToken` checks for `tokens.json` BEFORE the roster (below) — do not reorder.

Run the HostCommands tests; expect the four new ones RED (A8 trio: exit 0 instead of 4, or the wrong message; A3/A5: README lacks the roster). Show it.

**GREEN.** `src/ChopItUp.Hub/Hosting/HostCommands.cs`:

Replace the Task 2 stub with a real read that refuses to migrate:

```csharp
    /// <summary>The roster as the last hub start left it, read through a plain connection that runs
    /// NO pragmas. Never migrates and leaves the file byte-identical: a non-serving verb must have no
    /// write side effect (pass 2, MINOR-12 for tokens; the same rule for the schema).
    /// <see cref="ChopDb.Open"/> is not used on purpose — its <c>PRAGMA journal_mode=WAL</c> rewrites
    /// the header of a database that is not already WAL (a restored .bak is exactly that). Nor is
    /// <c>Mode=ReadOnly</c>: measured on Microsoft.Data.Sqlite 10.0.11, a read-only open of a WAL
    /// database whose -wal/-shm are absent creates both and does not remove them on close, so the
    /// verb would litter the data dir. <c>ReadWrite</c> (no Create) with no pragmas creates the
    /// sidecars on open and deletes them on close, leaves a cleanly-closed rollback-journal database
    /// untouched (a hot journal would be rolled back on open, which is SQLite recovery, not this
    /// verb's doing), and reads correctly beside a running hub. A database below the current version, above it, or
    /// absent is an exit-4 "start the hub once" / "run a newer build", mirroring the
    /// missing-tokens.json case.</summary>
    private static IReadOnlyList<string>? TryReadRoster(HubOptions options, TextWriter error, out IReadOnlyList<Participant> roster)
    {
        roster = [];
        var dbPath = Path.Combine(options.DataDir, "chopitup.db");
        if (!File.Exists(dbPath))
        {
            error.WriteLine($"No chopitup.db in '{options.DataDir}'. Start the hub once against this data directory first, or check --data.");
            return null;
        }
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();   // no PRAGMAs: see the summary
        int version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(cmd.ExecuteScalar());
        }
        if (version < ChopDb.LatestSchemaVersion)
        {
            error.WriteLine($"'{dbPath}' is at schema v{version}; this build needs v{ChopDb.LatestSchemaVersion}. Start the hub once to migrate it and mint the new tokens, then re-run this command.");
            return null;
        }
        if (version > ChopDb.LatestSchemaVersion)
        {
            error.WriteLine($"'{dbPath}' is at schema v{version}; this build understands v{ChopDb.LatestSchemaVersion}. Run a newer build.");
            return null;
        }
        roster = ParticipantStore.ReadAll(conn);
        return roster.Select(p => p.Id).ToArray();
    }
```

  (`using ChopItUp.Core.Model; using ChopItUp.Core.Storage; using Microsoft.Data.Sqlite;`.) `ParticipantStore.ReadAll(SqliteConnection)` is the static Task 1 defined. A locked, corrupt or busy file surfaces as `SqliteException`, which both verbs must turn into one line, not a stack trace: add `SqliteException` to `PrintConfig`'s existing `catch ... when (e is ...)` list (exit 3) and give `RotateToken` a `catch (SqliteException e) { error.WriteLine($"Could not read the roster: {e.Message}"); return 3; }`. Put the `TryReadRoster` calls INSIDE the `try` blocks so those catches cover them. The two A8 tests that snapshot names and bytes are what prove the "no side effect" claim; if either goes red, the connection mode is wrong — do not weaken the test.

`RotateToken`: keep the lock check first (exit 5). Then, before touching the roster, the same `tokens.json` existence check `PrintConfig` has (exit 4, message naming `tokens.json`) — `A6b` at line 74 asserts that message and must keep passing. Then inside the `try`: `if (TryReadRoster(options, error, out _) is not { } ids) return 4;` and `TokenStore.Rotate(options.DataDir, ids, options.RotateParticipant!)`. Order: lock → tokens.json → roster → rotate.

`PrintConfig`: after the `tokens.json` existence check, inside the `try`: `if (TryReadRoster(options, error, out var roster) is not { } ids) return 4;`, then `TokenStore.ReadExisting(options.DataDir, ids)` and `HostConfigs.Write(options.DataDir, port, tokens, roster)`.

`src/ChopItUp.Hub/Hosting/HostConfigs.cs`:

```csharp
    public static string Write(string dataDir, int port, IReadOnlyDictionary<string, string> tokens, IReadOnlyList<Participant> roster)
    {
        var folder = Path.Combine(dataDir, FolderName);
        Directory.CreateDirectory(folder);
        var url = $"http://127.0.0.1:{port}/mcp";
        // One file per app-backed row: a model row with no model of its own is a window some
        // program opens on the room (Claude Desktop / Claude Code, the Codex app). Spawn rows
        // (model set) get no file; the hub itself is their client (M5). At most one app-backed
        // row per host, by construction of the seed; a second would overwrite the first here.
        foreach (var row in roster.Where(p => p.Kind == "model" && p.Model is null))
        {
            switch (row.Host)
            {
                case "claude": File.WriteAllText(Path.Combine(folder, "claude-desktop.json"), ClaudeDesktop(url, tokens[row.Id])); break;
                case "codex":  File.WriteAllText(Path.Combine(folder, "codex-config.toml"), Codex(url, tokens[row.Id])); break;
                default: throw new InvalidOperationException($"Participant '{row.Id}' has host '{row.Host}', which has no config template.");
            }
        }
        File.WriteAllText(Path.Combine(folder, "README.md"), Readme(url, port, roster));
        return folder;
    }
```

  (`using ChopItUp.Core.Model;`.) `Readme(string url, int port)` → `Readme(string url, int port, IReadOnlyList<Participant> roster)`. Insert a roster section after the existing "| File | Where it goes |" table by interpolating `{RosterTable(roster)}` on its own line:

```csharp
    /// <summary>Owner-facing roster. Never a token: this file is the one in the folder that is safe
    /// to read aloud.</summary>
    private static string RosterTable(IReadOnlyList<Participant> roster)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Roster");
        sb.AppendLine();
        sb.AppendLine("Participants are rows in `chopitup.db` (table `participants`), read once at hub start. A token");
        sb.AppendLine("exists for every row in `tokens.json`; only app-backed rows get a config file here. Rows with a");
        sb.AppendLine("model are spawned by the hub itself once spawning ships, so they have no file to paste.");
        sb.AppendLine();
        sb.AppendLine("| Id | Host | Model | File | Note |");
        sb.AppendLine("|----|------|-------|------|------|");
        foreach (var p in roster)
        {
            var file = p.Kind == "human" ? "none (the web UI)"
                : p.Model is not null ? "no file (hub-spawned)"
                : p.Host switch { "claude" => "`claude-desktop.json`", "codex" => "`codex-config.toml`", _ => "no template for this host" };
            sb.AppendLine($"| `{p.Id}` | {p.Host} | {p.Model ?? "—"} | {file} | {p.Note ?? ""} |");
        }
        sb.AppendLine();
        sb.AppendLine("To rotate any row's token: `ChopItUp.Hub --rotate-token <id>` with the hub stopped, then");
        sb.AppendLine("`--print-config` again. A row added by hand shows up in the web UI and in list_rooms at once,");
        sb.AppendLine("but gets its token and its line in the participation prompt at the next hub start.");
        return sb.ToString();
    }
```

  In the README's existing "Tokens:" paragraph, the first sentence becomes "Tokens: `ChopItUp.Hub --rotate-token <id>` mints a new one for any roster id and invalidates the old at the next hub start." Keep the rest. Update the class doc comment's first sentence to "...carrying this hub's port and each app-backed row's real token." The raw-string README is `$"""` interpolated; `RosterTable` output contains no braces, so it is safe to interpolate.

Update the existing `HostCommandsTests`:
- `A7` still expects three files — assertion unchanged; its comment gains one sentence: "Spawn rows (M8) add no files either: the hub is their client."

Run the Hub suite: expect 81 + 4 = 85 passed, `-warnaserror` clean.

Commit: `M8 task 3: print-config and rotate read the roster; README lists it`.

---

## Task 4 — Instructions and tool prose from the roster; `list_rooms` returns it

**RED first.** `tests/ChopItUp.Hub.Tests/ParticipationTests.cs`, replace the line `Assert.Contains("@owner, @claude or @codex", instructions);` with:

```csharp
        foreach (var p in ChopDb.SeedRoster) Assert.Contains("@" + p.Id, instructions);
        Assert.DoesNotContain("@owner, @claude or @codex", instructions);
        // Exactly one blank line between the roster paragraph and the rules: fails if the header is
        // written as one raw string with a trailing blank line (a raw string drops its final newline).
        Assert.Contains("host and model.\n\nTaking part", instructions);
```

  (`using ChopItUp.Core.Storage;`.) Add to `tests/ChopItUp.Hub.Tests/RoomToolsTests.cs`:

```csharp
    [Fact]
    public async Task M8_A4_list_rooms_returns_the_roster()
    {
        await using var claude = await _host.ClientFor("claude");
        var rooms = HubTestHost.Json(await claude.CallToolAsync("list_rooms", new Dictionary<string, object?>()));
        var participants = rooms.GetProperty("participants").EnumerateArray().ToArray();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), participants.Select(p => p.GetProperty("id").GetString()));
        var fable = participants.Single(p => p.GetProperty("id").GetString() == "fable");
        Assert.Equal("claude", fable.GetProperty("host").GetString());
        Assert.Equal("fable", fable.GetProperty("model").GetString());
        Assert.Equal("model", fable.GetProperty("kind").GetString());
        Assert.Equal("Fable", fable.GetProperty("display_name").GetString());
        // A4 says every row carries `model`: for an app-backed row it is present and null, not absent.
        var claude = participants.Single(p => p.GetProperty("id").GetString() == "claude");
        Assert.True(claude.TryGetProperty("model", out var model));
        Assert.Equal(JsonValueKind.Null, model.ValueKind);
    }

    [Fact]
    public async Task M8_A2_a_spawn_row_can_authenticate_and_post_today()
    {
        // The row is inert until M5 spawns it, but its token is real: a hand-run headless client
        // holding it must be a first-class participant already.
        await using var opus = await _host.ClientFor("opus");
        var posted = HubTestHost.Json(await opus.CallToolAsync("post_message", new Dictionary<string, object?> { ["room_id"] = "general", ["body"] = "hello from opus" }));
        Assert.Equal("opus", posted.GetProperty("author_id").GetString());
    }
```

  (`using ChopItUp.Core.Storage; using System.Text.Json;` in `RoomToolsTests.cs` if not already present.) Run: `ParticipationTests` RED on `@opus`; `RoomToolsTests` RED on the missing `participants` property. The `M8_A2` test is expected to pass already (Task 2 minted the token) — say so in the commit body; it guards A2 end-to-end.

**GREEN.** `src/ChopItUp.Hub/Mcp/Participation.cs`: the `const string Instructions` becomes a method. Every existing bullet is carried over verbatim; only the roster sentence and the "Address" bullet change:

```csharp
using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Mcp;

/// <summary>The participation prompt the hub ships to every host at initialize
/// (<c>McpServerOptions.ServerInstructions</c>). It is the one place the room's rules live: hosts
/// are configured against it rather than each being told the rules by hand. The roster lines are
/// generated from the database so the prompt can never name a participant that does not exist.</summary>
public static class Participation
{
    public static string Instructions(IReadOnlyList<Participant> roster)
    {
        var human = roster.Where(p => p.Kind == "human").Select(p => $"{p.Id} (the human)");
        var models = roster.Where(p => p.Kind != "human").Select(p => $"{p.Id} ({p.DisplayName})");
        var everyone = string.Join(", ", human.Concat(models));
        var mentions = string.Join(", ", roster.Select(p => "@" + p.Id));
        return $"""
            You are a participant in Chop It Up, a shared chat hub running on one person's machine.
            The participants are {everyone}. Everyone reads and writes the same rooms through the
            tools on this server; list_rooms returns the roster with each participant's host and model.
            """ + "\n\n" + Rules.Replace("{MENTIONS}", mentions);
    }

    private const string Rules = """
        Taking part
        ...every existing bullet, verbatim, except:
        - Address a participant with @ and its id: {MENTIONS}. A message with no mention is for the
          room.
        ...through the closing "This is a working chat room..." paragraph, verbatim.
        """;
}
```

  "Verbatim" means: copy lines 13–48 of `Participation.cs` as they stand at HEAD (`f02c413`; from `Taking part` through the last line of the closing paragraph) into `Rules`, changing only the "Address someone with @owner, @claude or @codex." bullet. The plan does not carry that text; the repo does. The explicit `"\n\n"` is what keeps a blank line between the roster paragraph and `Taking part` — a raw string's final newline before the closing quotes is not part of the string, so a blank line left inside the header would not survive. `Rules` stays a plain (non-interpolated) raw string with a `{MENTIONS}` placeholder and `Replace`, so a future `{` in the prose cannot break compilation.

`src/ChopItUp.Hub/Hosting/HubHost.cs`: `o.ServerInstructions = Participation.Instructions` → `o.ServerInstructions = Participation.Instructions(roster)`.

`src/ChopItUp.Hub/Mcp/RoomTools.cs`:

- constructor: `public sealed class RoomTools(MessageStore store, ParticipantStore participants, MessageSignal signal, IHttpContextAccessor http)`.
- `list_rooms` `Description`: "List the chat rooms in this hub with message counts and how many messages you have not read yet. Also tells you which participant you are and returns the roster: every participant's id, display name, kind (human or model), host and model."
- `ListRooms` return: `JsonSerializer.Serialize(new { You = me, Participants = participants.List().Select(p => new { p.Id, p.DisplayName, p.Kind, p.Host, p.Model }), Rooms = rooms }, RosterJsonOptions)` where `RosterJsonOptions` is a second static options object: `new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }` — WITHOUT `WhenWritingNull`, because the class-wide `JsonOptions` (line 25) would drop `model` for the three rows where it is null, and A4 promises the key on every row. Rooms carry no nulls, so their output is unchanged. (`Note` is owner-facing; not sent to models.)
- `post_message` `Description`: replace "Mention a participant with @claude, @codex or @owner when the message is for them." with "Mention a participant with @ and its id (list_rooms returns the roster) when the message is for them." (Claim 13: the attribute must stay a constant, so it points at `list_rooms` instead of listing ids.)

Grep `src/ChopItUp.Hub` `*.cs` files for `@claude`, `@codex`, `@owner`: after this task there are no hits (the client's `markdown.ts:31` comment mentions `` `@claude` `` as an example of code-span text; Task 7 rewords it to `` `@someone` ``). `RoomTools`'s new constructor parameter resolves by DI because Task 2 registered `ParticipantStore`.

Run the Hub suite: expect 85 + 2 = 87 passed.

Commit: `M8 task 4: instructions, tool prose and list_rooms generated from the roster`.

---

## Task 5 — `ChatApi` posts as the human row; `GET /api/participants`

**RED first.** In `tests/ChopItUp.Hub.Tests/ChatApiTests.cs` add (match the file's fixture field name; if it is not `_host`, adapt — do not STOP for that):

```csharp
    [Fact]
    public async Task M8_A6_api_participants_returns_the_roster_in_camel_case()
    {
        var response = await _host.Client.GetAsync("/api/participants");
        response.EnsureSuccessStatusCode();
        var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToArray();
        Assert.Equal(ChopDb.SeedRoster.Select(p => p.Id), rows.Select(r => r.GetProperty("id").GetString()));
        var owner = rows.Single(r => r.GetProperty("id").GetString() == "owner");
        Assert.Equal("human", owner.GetProperty("kind").GetString());
        Assert.Equal("Owner", owner.GetProperty("displayName").GetString());
        var sol = rows.Single(r => r.GetProperty("id").GetString() == "gpt-5.6-sol");
        Assert.Equal("codex", sol.GetProperty("host").GetString());
        Assert.Equal("gpt-5.6-sol", sol.GetProperty("model").GetString());
    }
```

  (`using ChopItUp.Core.Storage;` if not present.) RED: 404.

**GREEN.** `src/ChopItUp.Hub/Web/ChatApi.cs`:

- `MapChatApi`: add `api.MapGet("/participants", GetParticipants);`
- add:

```csharp
    private static IResult GetParticipants(ParticipantStore participants) =>
        Results.Json(participants.List().Select(p => new { p.Id, p.DisplayName, p.Kind, p.Host, p.Model }));
```

- `PostMessage` and `PostImport` gain a `ParticipantStore participants` parameter and post as `participants.HumanId()` instead of `"owner"`. Update the two comments: "authored as the roster's one human row (D1 still: never the label in the text)". Also the `SpeakerHeader` doc comment at line 18, `authored by (that is always "owner", per D1)` → `authored by (that is always the roster's human row, per D1)` — the A7 grep reads comments too. `HumanId()` runs one query per post on a twelve-row table; it keeps the "exactly one human" invariant enforced at the write site rather than cached from startup.

Run the Hub suite: expect 88 passed. Every existing `ChatApiTests` assertion on `"owner"` still holds because the seed human is `owner`.

Commit: `M8 task 5: web API posts as the human row and serves the roster`.

---

## Task 6 — Dry run rehearses the real upgrade; self-check follows v3

The owner's live database is at v2 with a three-key `tokens.json`. The dry run today fabricates a v1 corpus and an empty data dir, so it rehearses v1→v3 from scratch — not the transition the real data will take. This task makes the real-binary rehearsal match A1/A2: a v2 corpus, a pre-existing three-key `tokens.json`, then the built hub. v1→v3 keeps its in-process coverage (`SchemaMigrationTests`).

**Corpus tool.** `tools/ChopItUp.Corpus/CorpusBuilder.cs`:

- `Build(string dataDir, int messages, int rooms, int leaveInWal, int? seed = null)` → `Build(string dataDir, int messages, int rooms, int leaveInWal, int? seed = null, int schemaVersion = 1)`; `if (schemaVersion is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The corpus builder writes v1 or v2.");`. Pass it to `CreateSchemaAndSeeds(writer, roomIds, schemaVersion)`.
- `CreateSchemaAndSeeds`: when `schemaVersion == 2`, the `messages` CREATE gains `, client_key TEXT` after `created_at TEXT NOT NULL`, the DDL gains `CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;` after the existing index, and the stamp is `PRAGMA user_version = 2;`. Build the two variants with two string variables (`clientKeyColumn`, `clientKeyIndex`) interpolated into the existing `$"""` block; the v1 output must stay byte-for-byte what it is today (the `DryRunTests` in-process path depends on it). Update the class doc comment: "...in the OLD (v1, or v2 with `--schema-version 2`) on-disk shape".
- `InsertMessages` needs no change (v2's `client_key` is nullable; inserts without it are valid v2 rows).

`tools/ChopItUp.Corpus/Program.cs` `RunBuild`: `int schemaVersion = int.Parse(args.GetValueOrDefault("--schema-version", "1"), CultureInfo.InvariantCulture);` and pass it as the last argument to `CorpusBuilder.Build`; add `--schema-version` to the verb comment at the top.

**Dry run.** `tools/Invoke-M2DryRun.ps1`:

- Step 2's `$corpusArgs` gains `'--schema-version', '2'`; the `corpus.seeded` detail gains "schema v2".
- New step between Step 2 and Step 4, "Step 3: the tokens the owner already pasted": write a three-key `tokens.json` into `$dataDir` BEFORE the hub starts, so the start is an upgrade, not a first run. Values are fabricated, never printed, and added to `$knownTokens` so the existing end-of-run "no token in output" sweep covers them:

```powershell
    # --- Step 3: a tokens.json from the previous build, so the hub start is an UPGRADE ------------
    $preTokens = [ordered]@{}
    foreach ($id in @('owner', 'claude', 'codex')) {
        $bytes = New-Object byte[] 32
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $preTokens[$id] = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $knownTokens.Add($preTokens[$id])
    }
    $preTokensPath = Join-Path $dataDir 'tokens.json'
    ($preTokens | ConvertTo-Json) | Set-Content -LiteralPath $preTokensPath -Encoding utf8
    Add-Check -Name 'tokens.pre-existing-written' -Passed (Test-Path $preTokensPath) -Detail '3 keys, previous-build shape'
```

- line 133: `-Passed ($health.schema -eq 2)` → `-Passed ($health.schema -eq 3)`, comment `# /health reports schema 3.`
- line 150: the check named `backup.stamped-v1` becomes `backup.stamped-v2` with `-eq 2` (the corpus is v2 now; the backup is taken from v2). Line 160's skipped-branch name follows.
- after the `print-config.three-files-written` check (line ~229; the folder variable is `$hostConfigsDir`, defined at line 224), add:

```powershell
    $readme = Get-Content -LiteralPath (Join-Path $hostConfigsDir 'README.md') -Raw
    Add-Check -Name 'readme.roster' -Passed (($readme -match '\| `gpt-6-astra` \|') -and ($readme -match '\| `fable` \|')) -Detail 'README lists the spawn rows'
    $postTokens = Get-Content -LiteralPath (Join-Path $dataDir 'tokens.json') -Raw | ConvertFrom-Json
    $tokenKeys = @($postTokens.PSObject.Properties).Count
    Add-Check -Name 'tokens.roster' -Passed ($tokenKeys -eq 12) -Detail "tokens.json keys=$tokenKeys"
    $preserved = $true
    foreach ($id in $preTokens.Keys) { if ($postTokens.$id -cne $preTokens[$id]) { $preserved = $false } }
    Add-Check -Name 'tokens.preserved' -Passed $preserved -Detail 'owner/claude/codex values unchanged across the upgrade'
    foreach ($p in $postTokens.PSObject.Properties) { if (-not $knownTokens.Contains($p.Value)) { $knownTokens.Add($p.Value) } }
```

  The last line registers the nine minted tokens with the output sweep too.

**Self-check.** `tools/Invoke-M4SelfCheck.ps1` line 321: `Add-Check -Name 'c3.health-schema-2' -Passed ($health.schema -eq 2)` → `Add-Check -Name 'c3.health-schema' -Passed ($health.schema -eq 3)`; line 25's prose "/health reports schema 2" → "schema 3". Nothing else in that script depends on the roster (claim 11 covers the token read; this line was outside its grep).

Run `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` (the corpus project is in the solution) and `dotnet test tests/ChopItUp.Hub.Tests --filter "FullyQualifiedName~DryRunTests" -c Debug --nologo -v minimal` (the in-process v1 path must still pass), then `pwsh -NoProfile -File tools/Invoke-M2DryRun.ps1` from the repo root. Expected: every check PASS, exit 0, including `health.schema` at 3, `backup.stamped-v2`, `readme.roster`, `tokens.roster`, `tokens.preserved`, and the existing no-token-in-output sweep. Paste the check lines into the commit body.

Commit: `M8 task 6: dry run rehearses v2 -> v3 with pre-existing tokens; self-check expects v3`.

---

## Task 7 — Client renders the fetched roster (opus builder: owner-visible)

No unit-test seam exists in the client (no test runner in `package.json`); the gate is `npm run build` (tsc + vite) plus the browser-pane check below. RED is the typecheck: change `types.ts`, `api.ts` and delete `MENTIONABLE` first; `markdown.ts`/`Thread.tsx` then fail `tsc` until rewritten.

`src/ChopItUp.Hub/client/src/types.ts` — append:

```ts
/** Mirrors `GET /api/participants`. `host` is which program speaks for the row; `model` is null for
 *  the human and for app-backed rows. */
export interface Participant {
  id: string;
  displayName: string;
  kind: 'human' | 'model';
  host: string;
  model: string | null;
}
```

`api.ts` — add (and extend the type import):

```ts
export async function listParticipants(signal?: AbortSignal): Promise<Participant[]> {
  return unwrap<Participant[]>(await fetch('/api/participants', { signal }));
}
```

`participants.ts` — rewrite. No participant id appears in this file; the badge overrides are keyed by host (a program name), and `p-owner`/`OW` are the pre-existing CSS token and badge for the human, not ids:

```ts
import type { Participant } from './types';

/** The roster as the hub reported it. Set once at startup (App.tsx); every lookup below reads it.
 *  Until it arrives, unknown ids fall back to the id itself, so a message never renders blank. */
let roster = new Map<string, Participant>();
let mention: RegExp | null = null;

export function setRoster(list: Participant[]): void {
  roster = new Map(list.map((p) => [p.id.toLowerCase(), p]));
  mention = list.length === 0 ? null : new RegExp(`@(${list.map((p) => escape(p.id)).join('|')})(?!\\.?[\\w-])`, 'gi');
}

/** Ids like `gpt-5.5` carry regex metacharacters; the alternation must match them literally. */
function escape(id: string): string {
  return id.replace(/[.*+?^${}()|[\]\\-]/g, '\\$&');
}

/** `null` before the roster has loaded: nothing is decorated rather than something wrong. The
 *  lookahead `(?!\.?[\w-])` rejects `@gpt-5.5-x` and `@gpt-5.5.x` (an id continues) but accepts
 *  `@opus.` and `@claude,` (a sentence ends) — the old `\b` accepted the trailing period and so must
 *  this. Callers reset `lastIndex`. */
export function mentionPattern(): RegExp | null {
  return mention;
}

/** The host family an id belongs to, for colour: `human`, `claude`, `codex`, or `other`. */
export function hostOf(authorId: string): string {
  const p = roster.get(authorId.toLowerCase());
  if (!p) return 'other';
  return p.kind === 'human' ? 'human' : p.host === 'claude' || p.host === 'codex' ? p.host : 'other';
}

export function displayName(authorId: string): string {
  const p = roster.get(authorId.toLowerCase());
  if (!p) return authorId;
  return p.kind === 'human' ? 'You' : p.displayName;
}

/** Two characters. Hosts keep the badges the UI shipped with, so app-backed rows look as they did;
 *  a spawn row takes the initials of its display name ("GPT-6 Astra" → GA, "Opus" → OP). */
const HOST_BADGE: Record<string, string> = { human: 'OW', claude: 'CL', codex: 'CX' };

export function badgeFor(authorId: string): string {
  const p = roster.get(authorId.toLowerCase());
  if (!p) return authorId.slice(0, 2).toUpperCase();
  if (p.model === null) return HOST_BADGE[p.host] ?? p.displayName.slice(0, 2).toUpperCase();
  const words = p.displayName.split(/\s+/).filter(Boolean);
  const initials = words.length >= 2 ? words[0]![0]! + words[1]![0]! : p.displayName.slice(0, 2);
  return initials.toUpperCase();
}

/** Drives `--accent` in styles.css. Colour is per host family: an `opus` row shares the Claude
 *  accent, a `gpt-*` row the Codex accent; name and badge tell rows of one family apart. */
export function accentClass(authorId: string): string {
  const host = hostOf(authorId);
  return host === 'human' ? 'p-owner' : `p-${host}`;
}

export function isHuman(authorId: string): boolean {
  return roster.get(authorId.toLowerCase())?.kind === 'human';
}
```

  Badge collisions in the seed: `GPT-5.5` has one word → `GP`; every other row is distinct (OW CL CX OP SO FA GA GS GT GL GP GM). Acceptable; the name is beside the badge on every run start.

`markdown.ts`:
- replace `import { MENTIONABLE } from './participants';` and the module-level `MENTION` constant with `import { hostOf, mentionPattern } from './participants';`.
- in `decorateMentions`, start with `const pattern = mentionPattern(); if (!pattern) return;` and use `pattern` where `MENTION` was used, resetting `pattern.lastIndex = 0` before each scan exactly as the existing code handles `MENTION` (read the function first).
- line 54 `span.dataset['who'] = match[1]!.toLowerCase();` → `span.dataset['host'] = hostOf(match[1]!);` — the mention chip's colour is keyed by host family, the same axis as the message row, so `@opus` and an `opus` row agree.
- line 31's comment example `` `@claude` inside a snippet `` → `` `@someone` inside a snippet `` (no participant id in client source, A7).

`styles.css` lines 535–543: the three rules `.mention[data-who='owner']`, `.mention[data-who='claude']`, `.mention[data-who='codex']` become `.mention[data-host='human']`, `.mention[data-host='claude']`, `.mention[data-host='codex']`, bodies unchanged. Check nothing else in the file or in `markdown.ts` reads `data-who` (grep; expected: no hits after the edit).

`Thread.tsx` line 70: `const mine = message.authorId.toLowerCase() === 'owner';` → `const mine = isHuman(message.authorId);` (import it).

`App.tsx`: add `import { setRoster } from './participants';` (the file imports nothing from there today). The roster is loaded BEFORE the first room is selected, so no message ever renders without it — no flash, no re-render plumbing. The existing startup effect (lines 55–61 at HEAD) becomes:

```ts
  useEffect(() => {
    const abort = new AbortController();
    api
      .listParticipants(abort.signal)
      .then((list) => {
        setRoster(list);
        return refreshRooms(abort.signal);
      })
      .catch((failure) => {
        if (!abort.signal.aborted) setError(api.describeError(failure));
      });
    return () => abort.abort();
  }, [refreshRooms]);
```

  `refreshRooms` is what sets `roomId`, and the message load effect keys on `roomId`, so ordering the roster first is sufficient. Decided, not left to the builder: no `rosterVersion`, no `key` remount, no cache invalidation — a roster that fails to load surfaces as the same error banner a failed room list does, and the room list is not shown until the roster is in. The focus-refresh effect (`refreshRooms` on `window` focus) is unchanged: it does not re-fetch the roster (Decision 5: new rows appear at the next hub start as far as tokens and instructions go; the client learns them on reload).

Build: `npm --prefix src/ChopItUp.Hub/client run build` — expect `tsc` clean and a vite bundle; then `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` (the csproj runs the client build, LESSONS M3).

**Browser-pane check (builder).** Run the hub from the repo against the gitignored `.data\` (absent at HEAD; if it exists from an earlier run that is fine — every query below is scoped to the LAST row, so earlier messages do not matter). Create `.claude/launch.json` if absent: `{"version":"0.0.1","configurations":[{"name":"hub","runtimeExecutable":"dotnet","runtimeArgs":["run","--project","src/ChopItUp.Hub","--","--data",".data"],"port":8790}]}`. Open the room and post `hi @opus, @gpt-5.5 and @gpt-5.5-nope. cc @claude.` from the composer; run BOTH `javascript_tool` queries below now (they read the last row, which is this one). Then post one message as `opus` through the MCP endpoint and run the first query again. The token is read into a variable inside the same command and never echoed (the repo's own dev `.data\tokens.json`, not the deployed install's):

```powershell
$t = (Get-Content .data\tokens.json -Raw | ConvertFrom-Json).opus
$h = @{ Authorization = "Bearer $t"; Accept = 'application/json, text/event-stream' }
$body = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"post_message","arguments":{"room_id":"general","body":"opus here"}}}'
(Invoke-WebRequest -Uri http://127.0.0.1:8790/mcp -Method Post -Headers $h -ContentType 'application/json' -Body $body).StatusCode
```

  (Stateless transport: no `initialize` handshake is required for a single `tools/call`; if the hub answers 4xx, send an `initialize` request first with the same headers, then retry — reuse nothing between calls.)

  Verify with `javascript_tool`, because `read_page` returns an accessibility tree that cannot see class names or the `.mention` span:

```js
(r => ({ cls: r.className, author: r.querySelector('.author')?.textContent, badge: r.querySelector('.avatar')?.textContent }))([...document.querySelectorAll('article.row')].at(-1))
```

  expected after the human post: `cls` contains `p-owner` and `mine`, `author` "You", `badge` "OW"; after the opus post: `cls` contains `p-claude` and NOT `mine`, `author` "Opus", `badge` "OP". (If the opus message continues a run — same author within 8 minutes — `author`/`badge` are null by design; it follows a human message here, so it starts a run.)

```js
[...[...document.querySelectorAll('article.row')].at(-1).querySelectorAll('.mention')].map(m => [m.textContent, m.dataset.host])
```

  expected exactly, on the human row: `["@opus","claude"]`, `["@gpt-5.5","codex"]`, `["@claude","claude"]` — three chips; `@gpt-5.5-nope` produces none. Paste all three results verbatim into the report. STOP-and-report with exactly what was verified if either post fails.

Commit: `M8 task 7: client renders the roster from /api/participants`.

---

## Verification (orchestrator, after Task 7)

1. `Check-PlanClaims.ps1` clean at Phase-B entry; suite 111 → 33+4 Core, 78+10 Hub = **125** expected (task deltas: 1:+4, 2:+3, 3:+4, 4:+2, 5:+1; reconciled assertions change no counts).
2. Guard tests: `SchemaMigrationTests` v1→v3 and v2→v3, torn-v3 repair; `TokenStoreTests` byte-identical backfill; `HostCommandsTests` byte-identical database on a refused verb.
3. Synthetic-corpus dry run: `tools/Invoke-M2DryRun.ps1` exit 0 — v2 corpus, pre-existing tokens preserved, backup stamped v2.
4. A7 grep, run from the repo root and proven able to go red first by planting `"codex"` in a scratch `.cs` under `src/` and seeing it counted, then removing it:
   `$hits = Get-ChildItem src -Recurse -Include *.cs,*.ts,*.tsx | ? FullName -notmatch 'node_modules|\\bin\\|\\obj\\' | Select-String -Pattern '"owner"|"claude"|"codex"|''owner''|''claude''|''codex''' | ? Path -notmatch 'ChopDb\.cs|HostConfigs\.cs|participants\.ts'; $hits; $hits.Count` — expected `0`. Exclusions are legitimate host-name (not participant-id) literals: `HostConfigs.cs` switches on host and names files; `participants.ts` maps host → badge/accent. Comments count (Task 5 rewords `ChatApi.cs:18`; Task 7 rewords `markdown.ts:31`).
5. Screenshot judge (sonnet, text verdict) on the builder's browser-pane captures plus the two `javascript_tool` results: names "You"/"Opus", badges "OW"/"OP", accents differ, three mention chips with the right hosts.
6. UIA interactive gate is N/A (web UI; the browser pane covers click and hover); say so in the ping.
7. `mattpocock-skills:code-review` on the branch (Standards + Spec axes, no agents).
8. Deploy: `tools\Deploy-ChopItUp.ps1` (harness ask on writes to the stable launch dir is expected), start the exe once — that run migrates the live database to v3 (backup `chopitup.db.v2.<stamp>.bak` beside it) and mints nine tokens — then `--print-config` and confirm the README roster; the two pasted host configs stay valid because the `claude`/`codex` tokens are untouched (A2). Confirmation reads `/health` (`schema: 3`) and the exe's stdout, never the deployed data folder (privacy guard).

## Critique dispositions

Pass 1 (opus, 2026-09-05): FIX-THEN-SHIP, 6.8. Every finding and what was done:

| # | Finding | Disposition |
|---|---------|-------------|
| M1 | Five three-row assertions unswept (`ChopDbTests:32,:50`, `SchemaMigrationTests:86`, `HubHostTests:23-24`) | Fixed: reconciliation bullets in Tasks 1 and 2; counts recomputed (125). |
| M2 | `A6b` breaks if the roster check precedes the tokens.json check in `RotateToken` | Fixed: order is lock → tokens.json → roster → rotate, stated. |
| M3 | `Invoke-M4SelfCheck.ps1:321` asserts schema 2 | Fixed: Task 6 edits it. |
| M4 | `TryReadRoster` via `ChopDb.Open()` writes the WAL header; `SqliteException` uncaught; name-only snapshot blind | Fixed: pragma-free connection + `ParticipantStore.ReadAll(conn)` (pass 1 chose `ReadOnly`; pass 2 measured that as littering and corrected it to `ReadWrite`, no Create); `SqliteException` → exit 3 in both verbs; A8 test compares database bytes. |
| M5 | Verification step 4 used a non-existent `Select-String -Recurse`; `participants.ts` host literals would hit | Fixed: `Get-ChildItem \| Select-String`, exclusion named, red-first proof required. |
| M6 | Lookahead `(?![\w.-])` dropped sentence-final mentions (`@claude.`) | Fixed: `(?!\.?[\w-])`; `@claude.` added to the browser check. |
| M7 | Mention chips keyed by `data-who` = old ids; `styles.css` not in Task 7 | Fixed: `hostOf()` + `data-host`; CSS re-keyed; file added. |
| M8 | `read_page` cannot see classes or `.mention` spans | Fixed: `javascript_tool` queries with exact expected output. |
| M9 | Dry run rehearsed v1→v3 from scratch, not the owner's v2 + three tokens | Fixed: corpus `--schema-version 2`, pre-written tokens.json, `tokens.preserved`, `backup.stamped-v2`. |
| M10 | `WhenWritingNull` drops `model` on three rows; test blind | Fixed: `RosterJsonOptions` without the ignore condition; test asserts present-and-null. |
| m11 | HubHost range 44–50 swallowed `MessageSignal` | Fixed: 44–49, lines 50–51 named. |
| m12 | `StartedOnce` sites listed as "bare lines" | Fixed: rule + every line number. |
| m13 | Bearer token on a command line | Fixed: variable inside one command, never echoed. |
| m14 | Nine "paste it into that host's config" lines on upgrade | Fixed: message reworded, roster-free. |
| m15 | No `>` arm in `TryReadRoster` | Fixed, with a test. |
| m16 | Claim 12 is Codex's self-report; Decision 4's FK claim overstated | Declined for claim 12: F7 records Codex ran `model/list` itself and the row is data if wrong. Decision 4 reworded. |
| m17 | Decision 7 should be provisional | Fixed: marked provisional. |
| n18–n21 | usings, `$hostConfigsDir`, README else-arm, `$LASTEXITCODE` | All fixed. |

Pass 2 (fable, 2026-09-05): FIX-THEN-SHIP, 6.4. Findings and dispositions:

| # | Finding | Disposition |
|---|---------|-------------|
| M1 | `Mode=ReadOnly` open of a WAL db creates `-wal`/`-shm` and leaves them (measured on M.D.Sqlite 10.0.11); two tests red, data dir littered | Fixed: `ReadWrite` (no Create), no pragmas — measured clean in all four scenarios; doc comment carries the measurement. |
| M2 | Fresh db: V1 seeds the three rows without `note`, so `Assert.Equal(SeedRoster, List())` is red | Fixed: `BackfillNotes` in V3 (NULL only); "net effect" paragraph corrected. |
| M3 | Six more `Assert.Equal(2, GetSchemaVersion())` + `DryRunTests:30` unswept | Fixed: all seven listed in Task 1. |
| m4 | `HubHostTests:22` `Load(_dir)` compile error | Fixed; every `Load(` site enumerated. |
| m5 | `ChatApi.cs:18` comment hits the A7 grep; `node_modules` enumerated; A7 text vs exclusions | Fixed: comment reworded in Task 5; grep filters; A7 sentence names the host-literal exclusions. |
| m6 | `markdown.ts:31` comment has `@claude` | Fixed in Task 7; Task 4 grep scoped to `*.cs`. |
| m7 | `Rules` "verbatim" needs the repo; header loses its blank line | Fixed: "copy lines 13–48 at HEAD"; explicit `"\n\n"`. |
| m8 | Decision 5 says startup-static; three surfaces are live | Fixed: decision reworded to say which is which; README sentence follows. |
| m9 | Exact-count mention query on a growing room | Fixed: every query scoped to the last row. |
| m10 | Pre-roster flash accepted as the acceptance; remount-vs-prop left to the builder | Fixed: roster loads before the first room is selected; `rosterVersion`/`key`/`invalidateRendered` removed; decided. |
| — | Tickets 03–07 out of date with the folded counts and Task 6/7 changes | Fixed: re-cut. |

Pass 3 (opus, narrow re-check of the folds, 2026-09-05): `RECHECK: 5 issues`, none gating, all folded — blank-line assertion added to `ParticipationTests`; `setRoster` import named for `App.tsx`; hot-journal clause in `TryReadRoster`'s comment; `HubHostTests:23-24` moved into Task 1 so its commit leaves the Hub suite green; `styles.css` range 535–543. Measured by the re-check: the `ReadWrite`/no-pragma open leaves listing and bytes identical in all four scenarios; fresh-db `SeedRoster` equality holds by rowid order; every count recomputes to 37 / 81 / 85 / 87 / 88 / 125.

## Could not verify in this environment

- The deployed install's live migration happens only at deploy; the guard tests and dry run stand in for it beforehand. The privacy guard denies reading that install's data folder in every session, so post-deploy confirmation is `/health` and stdout only.
- Codex's model list (claim 12) is Codex's own report; if `codex app-server model/list` disagrees at M5, the roster is data — `INSERT`/`UPDATE` rows, no code.
- Whether Claude Desktop re-reads server instructions without a full quit is not tested here; the README already says quit fully.
