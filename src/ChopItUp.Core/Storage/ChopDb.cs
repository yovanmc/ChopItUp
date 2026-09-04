using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

/// <summary>Owns the SQLite connection string and schema lifecycle. Connection-per-query,
/// pooling off, WAL + foreign_keys + busy_timeout on every open.</summary>
public sealed class ChopDb
{
    public const int LatestSchemaVersion = 1;

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
    /// interrupted first start can never leave tables without a stamp.</summary>
    public void EnsureDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
        PathMutex.Run("Global\\ChopItUp.Migrate.", DatabasePath, TimeSpan.FromSeconds(30), () =>
        {
            using var conn = Open();
            if (GetUserVersion(conn) < 1) ApplyV1(conn);
            return 0;
        });
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
}
