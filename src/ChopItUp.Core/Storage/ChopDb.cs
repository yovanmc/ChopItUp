using ChopItUp.Core.Model;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

/// <summary>Owns the SQLite connection string and schema lifecycle. Connection-per-query,
/// pooling off, WAL + foreign_keys + busy_timeout on every open.</summary>
public sealed class ChopDb
{
    public const int LatestSchemaVersion = 3;

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

    /// <summary>Path of the backup written by the most recent migration on this instance, or null
    /// when nothing needed migrating. Test seam; not part of the hub's runtime contract.</summary>
    public string? LastBackupPath { get; private set; }

    private readonly string _connectionString;

    public string DatabasePath { get; }

    public ChopDb(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Creates the schema on a fresh DB and applies migrations. Idempotent, and safe when two
    /// processes call it on the same path: the ladder runs under a per-path named mutex, and every
    /// step commits its DDL, its seed rows AND its <c>user_version</c> stamp in ONE transaction, so an
    /// interrupted start can never leave tables without a stamp. A database that is already stamped at
    /// a lower version is backed up first. Version 0 with no messages is repaired in place instead —
    /// there is nothing there yet worth saving — but version 0 that still holds messages is backed up
    /// like any other, because a <c>.dump</c>-rebuilt or hand-repaired database loses its stamp and
    /// keeps its data.</summary>
    public void EnsureDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
        PathMutex.Run("Global\\ChopItUp.Migrate.", DatabasePath, TimeSpan.FromSeconds(30), () =>
        {
            LastBackupPath = null;                        // per-call, not per-instance (pass 2, N3)
            SweepPartialBackups();
            using var conn = Open();
            int version = GetUserVersion(conn);

            // An older build must never touch a newer store. Without this, an exe left in
            // C:\Self Apps beside a newer repo build silently reads and writes a schema it does not
            // understand (pass 2, MINOR-14).
            if (version > LatestSchemaVersion)
                throw new InvalidOperationException(
                    $"'{DatabasePath}' is at schema v{version}; this build understands v{LatestSchemaVersion}. Run a newer build.");

            if (version >= LatestSchemaVersion) return 0;

            // Back up whenever there is anything to lose. Version 0 usually means "never finished
            // being created" — but a database rebuilt by `.dump`/`.read`, or hand-repaired, also
            // loses its user_version stamp while keeping every message (pass 2, MINOR-15).
            if (version > 0 || HasAnyMessages(conn))
                LastBackupPath = BackupBeforeMigration(conn, version);   // throws => nothing is migrated

            if (version < 1) ApplyV1(conn);
            if (GetUserVersion(conn) < 2) ApplyV2(conn);
            if (GetUserVersion(conn) < 3) ApplyV3(conn);
            return 0;
        });
    }

    /// <summary>True when the messages table exists AND holds a row. A database with no such table
    /// is a genuinely empty start.</summary>
    private static bool HasAnyMessages(SqliteConnection conn)
    {
        using var exists = conn.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='messages'";
        if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return false;
        return Count(conn, "SELECT COUNT(*) FROM messages") > 0;
    }

    /// <summary>A <c>.partial</c> is by definition an abandoned copy: it exists only between the
    /// start of a backup and its verification, and the process that owned it is gone.</summary>
    private void SweepPartialBackups()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!;
        foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(DatabasePath) + ".*.bak.partial*"))
            try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Test seam: how the backup destination is opened. Production passes the real thing;
    /// the A2 abort test injects one that throws, which is the only way to execute the abort path
    /// (pass 2, MAJOR-4 — a read-only source throws in <see cref="Open"/> and never reaches here).</summary>
    internal Func<string, SqliteConnection> BackupDestinationFactory { get; set; } = path =>
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    };

    /// <summary>Online SQLite backup (not a file copy: the live database has a WAL, and copying the
    /// main file alone can capture a torn state). Written beside the database, under the same mutex,
    /// before any DDL runs, and then VERIFIED — integrity, version stamp and message-count parity —
    /// because an unverified backup is worse than none: it looks like a way back and is not.
    ///
    /// The copy is made under a <c>.bak.partial</c> name and renamed only after it verifies, so the
    /// <c>.bak</c> extension means "this one was checked". A process killed mid-copy runs no catch
    /// block; without the two-phase name it would leave a truncated file under the real name, and
    /// the restore procedure invites the owner to use exactly that file (pass 2, MAJOR-5).</summary>
    private string BackupBeforeMigration(SqliteConnection source, int fromVersion)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var final = $"{DatabasePath}.v{fromVersion}.{stamp}.bak";
        var partial = final + ".partial";
        TryDeleteBackup(partial);
        try
        {
            long expected = Count(source, "SELECT COUNT(*) FROM messages");
            using (var destination = BackupDestinationFactory(partial))
            {
                source.BackupDatabase(destination);
                VerifyBackup(destination, fromVersion, expected);
            }
            SqliteConnection.ClearAllPools();          // release the -wal/-shm the verify opened
            TryDeleteBackup(final);                    // same-second retry after an earlier failure
            File.Move(partial, final);
            return final;
        }
        catch
        {
            TryDeleteBackup(partial);
            throw;
        }
    }

    /// <summary>Three independent questions of the copy, asked while it is still open: is it a sound
    /// SQLite file, is it stamped at the version we are migrating away from, and does it hold every
    /// message the source held?</summary>
    internal static void VerifyBackup(SqliteConnection destination, int fromVersion, long expectedMessages)
    {
        using var check = destination.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        var result = check.ExecuteScalar() as string;
        if (!string.Equals(result, "ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"Pre-migration backup failed integrity check: {result ?? "(null)"}.");

        int version = GetUserVersion(destination);
        if (version != fromVersion)
            throw new InvalidOperationException($"Pre-migration backup is stamped v{version}, expected v{fromVersion}.");

        long actual = Count(destination, "SELECT COUNT(*) FROM messages");
        if (actual != expectedMessages)
            throw new InvalidOperationException($"Pre-migration backup holds {actual} messages, expected {expectedMessages}.");
    }

    /// <summary>The sidecars exist because verification opened the copy; a backup with a stray -wal
    /// beside it still restores correctly, but a half-written one must leave nothing behind. The
    /// catch is deliberately wider than IOException: this runs from inside the abort path's catch
    /// block, and an ACL failure here would replace the real backup-failure message — the owner's
    /// only lead — with a delete failure (pass 2, MINOR-16).</summary>
    private static void TryDeleteBackup(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static long Count(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public int GetSchemaVersion()
    {
        using var conn = Open();
        return GetUserVersion(conn);
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>IF NOT EXISTS / OR IGNORE so a pre-fix torn DB (tables present, stamp 0) is repaired
    /// rather than crashed on. The stamp is the last statement of the same transaction.</summary>
    private static void ApplyV1(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS participants (
                id           TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                kind         TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rooms (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                room_id    TEXT NOT NULL REFERENCES rooms(id),
                author_id  TEXT NOT NULL REFERENCES participants(id),
                body       TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_room_id ON messages(room_id, id);
            CREATE TABLE IF NOT EXISTS read_cursors (
                participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id        TEXT NOT NULL REFERENCES rooms(id),
                last_read_id   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id)
            );
            INSERT OR IGNORE INTO participants (id, display_name, kind) VALUES
                ('owner',  'Owner',  'human'),
                ('claude', 'Claude', 'model'),
                ('codex',  'Codex',  'model');
            INSERT OR IGNORE INTO rooms (id, name, created_at) VALUES ('general', 'General', $at);
            PRAGMA user_version = 1;
            """;
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(DateTimeOffset.UtcNow));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>v2 adds the retry key that makes <c>post_message</c> safe to repeat: an optional
    /// per-author key, unique per room. SQLite has no <c>ADD COLUMN IF NOT EXISTS</c>, so the column is
    /// probed inside the transaction — that is what makes a torn v2 re-runnable. The stamp is the last
    /// statement of the same transaction (LESSONS, M1).</summary>
    private static void ApplyV2(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        bool hasColumn;
        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'client_key'";
            hasColumn = Convert.ToInt64(probe.ExecuteScalar()) > 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = (hasColumn ? "" : "ALTER TABLE messages ADD COLUMN client_key TEXT;\n") + """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_client_key
                ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;
            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

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
}
