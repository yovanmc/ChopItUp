using ChopItUp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Tests.Storage;

/// <summary>Schema-evolution guard: a database written in the OLD on-disk shape, read by the NEW
/// code, with every meaning asserted unchanged. The fixture is raw SQL on purpose — it must keep
/// describing v1 even after ChopDb stops being able to produce a v1.</summary>
public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chopitup_mig_" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "chopitup.db");

    private void WriteRawV1()
    {
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
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            INSERT INTO participants (id, display_name, kind) VALUES ('owner','Owner','human'),('claude','Claude','model'),('codex','Codex','model');
            INSERT INTO rooms (id, name, created_at) VALUES ('general','General','2026-09-01T10:00:00.000+00:00');
            INSERT INTO messages (id, room_id, author_id, body, created_at) VALUES
                (1,'general','owner','first v1 message','2026-09-01T10:01:00.000+00:00'),
                (2,'general','claude','second v1 message','2026-09-01T10:02:00.000+00:00');
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ('codex','general',1);
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void A2_A3_v1_database_is_backed_up_then_migrated_with_every_meaning_intact()
    {
        WriteRawV1();
        var before = File.ReadAllBytes(DbPath);

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(2, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.True(File.Exists(db.LastBackupPath!));
        Assert.Contains(".v1.", Path.GetFileName(db.LastBackupPath!));

        // A2: the backup is a real database still readable at v1, holding the pre-migration rows.
        using (var backup = new SqliteConnection($"Data Source={db.LastBackupPath};Mode=ReadOnly;Pooling=False"))
        {
            backup.Open();
            using var q = backup.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM messages";
            Assert.Equal(2L, Convert.ToInt64(q.ExecuteScalar()));
            q.CommandText = "PRAGMA user_version";
            Assert.Equal(1L, Convert.ToInt64(q.ExecuteScalar()));
        }

        // A3: rows, ids, authors, bodies, timestamps and cursors are untouched.
        var store = new MessageStore(db);
        var page = store.Read("general", 0, 50);
        Assert.Equal(2, page.Messages.Count);
        Assert.Equal(new[] { 1L, 2L }, page.Messages.Select(m => m.Id));
        Assert.Equal(new[] { "owner", "claude" }, page.Messages.Select(m => m.AuthorId));
        Assert.Equal("first v1 message", page.Messages[0].Body);
        // Compare as instants, not as strings: Timestamps.Stamp emits round-trip "o" format with
        // SEVEN fraction digits, so it never equals the fixture's three-digit literal (pass 2,
        // MAJOR-7 — the earlier form could not have gone green).
        Assert.Equal(
            new[] { DateTimeOffset.Parse("2026-09-01T10:01:00.000+00:00"), DateTimeOffset.Parse("2026-09-01T10:02:00.000+00:00") },
            page.Messages.Select(m => m.CreatedAt.ToUniversalTime()));
        Assert.Equal(1L, store.GetCursor("codex", "general"));
        Assert.Single(store.ListRooms());
        Assert.Equal(3L, ScalarLong("SELECT COUNT(*) FROM participants"));   // seeds not duplicated by the ladder
        Assert.True(new FileInfo(DbPath).Length > 0 && before.Length > 0);
    }

    [Fact]
    public void A2_the_backup_captures_writes_still_sitting_in_the_WAL()
    {
        WriteRawV1();

        // A writer holds the connection open with checkpointing disabled, so the extra rows exist
        // ONLY in the -wal file. A file copy of chopitup.db would miss all of them; the online
        // backup must not. This is the case the whole BackupDatabase choice exists for.
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            writer.Open();
            using (var off = writer.CreateCommand())
            {
                off.CommandText = "PRAGMA wal_autocheckpoint=0;";
                off.ExecuteNonQuery();
            }
            using var tx = writer.BeginTransaction();
            using var ins = writer.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO messages (room_id, author_id, body, created_at) VALUES ('general','claude',$b,'2026-09-01T10:03:00.000+00:00')";
            var p = ins.Parameters.Add("$b", Microsoft.Data.Sqlite.SqliteType.Text);
            for (int i = 0; i < 1_000; i++) { p.Value = $"wal message {i}"; ins.ExecuteNonQuery(); }
            tx.Commit();

            var db = new ChopDb(DbPath);
            db.EnsureDatabase();
            Assert.NotNull(db.LastBackupPath);
            Assert.Equal(1002L, BackupScalar(db.LastBackupPath!, "SELECT COUNT(*) FROM messages"));
            Assert.Equal(2L, BackupScalar(db.LastBackupPath!, "SELECT COUNT(*) FROM messages WHERE body LIKE '%v1 message'"));
        }
    }

    [Fact]
    public void A8_fresh_directory_lands_on_v2_with_no_backup()
    {
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(2, db.GetSchemaVersion());
        Assert.Null(db.LastBackupPath);
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    [Fact]
    public void A3_running_the_ladder_again_is_a_no_op()
    {
        WriteRawV1();
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();          // -> v2, one backup
        db.EnsureDatabase();          // already current: no ALTER, no second backup
        Assert.Equal(2, db.GetSchemaVersion());
        Assert.Single(Directory.GetFiles(_dir, "*.bak"));
    }

    [Fact]
    public void A2_a_genuinely_torn_v2_finishes_instead_of_crashing()
    {
        // The real torn state: the ALTER landed, the process died before the stamp. The column is
        // there, user_version is still 1. Re-running must complete the step, not fail on a
        // duplicate column, and must still take a backup (the DB is stamped below latest).
        WriteRawV1();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE messages ADD COLUMN client_key TEXT;";
            cmd.ExecuteNonQuery();
        }
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(2, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Equal(2, new MessageStore(db).Read("general", 0, 50).Messages.Count);
    }

    [Fact]
    public void A2_a_backup_that_fails_aborts_the_migration_and_leaves_nothing_behind()
    {
        WriteRawV1();
        var db = new ChopDb(DbPath) { BackupDestinationFactory = _ => throw new IOException("disk full (injected)") };

        var ex = Assert.Throws<IOException>(() => db.EnsureDatabase());
        Assert.Contains("injected", ex.Message);

        // The database is untouched at v1 — the migration never started — and nothing on disk
        // claims to be a way back.
        Assert.Equal(1, new ChopDb(DbPath).GetSchemaVersion());
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }

    [Theory]
    [InlineData(1, 99, "holds")]        // count parity fails
    [InlineData(7, 2, "stamped")]       // version stamp fails
    public void A2_each_verification_leg_rejects_a_bad_copy(int fromVersion, long expectedMessages, string messageFragment)
    {
        // `BackupDatabase` replaces the destination wholesale, so a bad copy cannot be staged
        // through the factory — the verifier is asserted directly. `VerifyBackup` is `internal`
        // and the test project already has InternalsVisibleTo on the Hub; add it on Core too.
        WriteRawV1();
        using var copy = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        copy.Open();
        var ex = Assert.Throws<InvalidOperationException>(() => ChopDb.VerifyBackup(copy, fromVersion, expectedMessages));
        Assert.Contains(messageFragment, ex.Message);
    }

    [Fact]
    public void A2_an_earlier_verified_backup_is_kept_and_an_abandoned_partial_is_swept()
    {
        WriteRawV1();
        var earlier = DbPath + ".v1.20260101T000000Z.bak";
        File.Copy(DbPath, earlier);                                        // a real, verified-looking snapshot
        var abandoned = DbPath + ".v1.20260101T000000Z.bak.partial";
        File.WriteAllText(abandoned, "torn copy from a killed process");

        var db = new ChopDb(DbPath);
        db.EnsureDatabase();

        Assert.Equal(2, db.GetSchemaVersion());
        Assert.True(File.Exists(earlier));                                 // older snapshots are never destroyed
        Assert.False(File.Exists(abandoned));                              // torn ones never survive to look like snapshots
        Assert.Equal(2, Directory.GetFiles(_dir, "*.bak").Length);
    }

    [Fact]
    public void A2_a_stamp_less_database_that_still_holds_messages_is_backed_up()
    {
        // Version 0 does not always mean "never finished being created": a .dump/.read rebuild or a
        // hand repair loses the stamp and keeps every message (pass 2, MINOR-15).
        WriteRawV1();
        using (var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadWrite;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 0;";
            cmd.ExecuteNonQuery();
        }
        var db = new ChopDb(DbPath);
        db.EnsureDatabase();
        Assert.Equal(2, db.GetSchemaVersion());
        Assert.NotNull(db.LastBackupPath);
        Assert.Equal(2L, BackupScalar(db.LastBackupPath!, "SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public void A2_a_newer_database_is_refused_rather_than_opened()
    {
        WriteRawV1();
        using (var conn = new SqliteConnection($"Data Source={DbPath};Mode=ReadWrite;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {ChopDb.LatestSchemaVersion + 1};";
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<InvalidOperationException>(() => new ChopDb(DbPath).EnsureDatabase());
        Assert.Contains("newer build", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir, "*.bak"));
    }

    private long ScalarLong(string sql)
    {
        using var conn = new ChopDb(DbPath).Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long BackupScalar(string backupPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            Directory.Delete(_dir, recursive: true);
        }
    }
}
