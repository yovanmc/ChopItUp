using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Corpus;

/// <summary>A single fingerprint of a messages/read_cursors table: enough to prove a migration or a
/// backup lost or gained nothing, without comparing every row. <see cref="ToCanonicalJson"/> — not
/// record equality — is what callers compare, because <see cref="SortedDictionary{TKey,TValue}"/>
/// gives deterministic key order but record-generated Equals still compares dictionaries by
/// reference.</summary>
public sealed record Fingerprint(
    long Count,
    long MinId,
    long MaxId,
    long BodyLengthSum,
    SortedDictionary<string, long> PerRoomCounts,
    SortedDictionary<string, long> Cursors)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToCanonicalJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Holds the writer connection <see cref="CorpusBuilder.Build"/> used to seed a database,
/// kept open on purpose. Disposing performs SQLite's normal clean-close checkpoint, which flushes
/// every pending write — including the ones the builder deliberately left in the write-ahead log —
/// into the main file (verified empirically: a plain <c>Dispose()</c> removes the -wal file
/// entirely). Dispose only once the caller no longer needs the "still in the WAL" state, e.g. after
/// a migration under test has already run against the directory.</summary>
public sealed class CorpusHandle(Fingerprint fingerprint, SqliteConnection writer) : IDisposable
{
    public Fingerprint Fingerprint { get; } = fingerprint;

    public void Dispose()
    {
        writer.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>Fabricates a multi-room, multi-participant ChopItUp database in the OLD (v1, or v2 with
/// `--schema-version 2`) on-disk shape, entirely from raw SQL — never via <c>ChopDb</c>, so it keeps
/// describing v1 even after ChopDb stops being able to produce one (same reasoning as
/// <c>ChopItUp.Core.Tests.Storage.SchemaMigrationTests</c>'s fixture). Bodies are a fixed template
/// plus an index; nothing here resembles a real conversation. Shared by
/// <c>tools/Invoke-M2DryRun.ps1</c> (as the compiled CLI, see <c>Program.cs</c>) and
/// <c>DryRunTests.cs</c> (in-process, via project reference) so the two paths cannot drift.</summary>
public static class CorpusBuilder
{
    public const string DatabaseFileName = "chopitup.db";
    public static readonly string[] Participants = ["owner", "claude", "codex"];

    /// <summary>Builds the corpus and returns a handle whose <see cref="CorpusHandle.Fingerprint"/>
    /// describes it exactly as left on disk: <paramref name="leaveInWal"/> of the
    /// <paramref name="messages"/> total exist only in the write-ahead log (autocheckpoint is
    /// disabled before they are inserted, and a deterministic <c>wal_checkpoint(TRUNCATE)</c> runs
    /// just before that, so the split is exact, not approximate).</summary>
    public static CorpusHandle Build(string dataDir, int messages, int rooms, int leaveInWal, int? seed = null, int schemaVersion = 1)
    {
        if (messages < 0) throw new ArgumentOutOfRangeException(nameof(messages));
        if (rooms < 1) throw new ArgumentOutOfRangeException(nameof(rooms));
        if (leaveInWal < 0 || leaveInWal > messages) throw new ArgumentOutOfRangeException(nameof(leaveInWal), "leaveInWal must be between 0 and messages.");
        if (schemaVersion is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The corpus builder writes v1 or v2.");

        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, DatabaseFileName);
        if (File.Exists(dbPath))
            throw new InvalidOperationException($"'{dbPath}' already exists; the corpus builder only ever writes a fresh database.");

        var roomIds = Enumerable.Range(1, rooms).Select(i => i == 1 ? "general" : $"room-{i}").ToArray();
        var roomMessageIds = roomIds.ToDictionary(r => r, _ => new List<long>());

        var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        writer.Open();

        Exec(writer, "PRAGMA journal_mode=WAL;");
        CreateSchemaAndSeeds(writer, roomIds, schemaVersion);

        int checkpointed = messages - leaveInWal;
        var baseTime = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        InsertMessages(writer, roomIds, roomMessageIds, baseTime, startIndex: 0, count: checkpointed);

        // Deterministic split: everything inserted so far is flushed into the main file and the WAL
        // is truncated to empty, so exactly `leaveInWal` messages (inserted next, with autocheckpoint
        // off) can ever end up WAL-only — not "however many happened to still be buffered".
        Exec(writer, "PRAGMA wal_checkpoint(TRUNCATE);");
        Exec(writer, "PRAGMA wal_autocheckpoint=0;");

        InsertMessages(writer, roomIds, roomMessageIds, baseTime, startIndex: checkpointed, count: leaveInWal);
        InsertCursors(writer, roomIds, roomMessageIds);

        var fingerprint = ComputeFingerprint(writer);
        return new CorpusHandle(fingerprint, writer);
    }

    /// <summary>Fingerprints an existing database file (a verified backup, or the post-migration
    /// live database) through a fresh short-lived connection — safe to call while another process
    /// (the real hub) holds the same file open, since WAL readers never block on a writer.</summary>
    public static Fingerprint FingerprintOf(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return ComputeFingerprint(conn);
    }

    /// <summary>Runs <c>PRAGMA quick_check</c> and reads <c>PRAGMA user_version</c> on an existing
    /// database file, alongside its fingerprint — the three facts the dry run needs to judge a
    /// backup (or the migrated database) sound, correctly versioned and complete.</summary>
    public static (string QuickCheck, int UserVersion, Fingerprint Fingerprint) Inspect(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        var quickCheck = check.ExecuteScalar() as string ?? "(null)";

        using var version = conn.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var userVersion = Convert.ToInt32(version.ExecuteScalar());

        return (quickCheck, userVersion, ComputeFingerprint(conn));
    }

    private static void CreateSchemaAndSeeds(SqliteConnection conn, string[] roomIds, int schemaVersion)
    {
        var roomValues = string.Join(",\n                ",
            roomIds.Select(r => $"('{r}', '{RoomName(r)}', '{Stamp(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))}')"));

        var clientKeyColumn = schemaVersion == 2 ? ", client_key TEXT" : "";
        var clientKeyIndex = schemaVersion == 2
            ? "CREATE UNIQUE INDEX ux_messages_client_key ON messages(room_id, author_id, client_key) WHERE client_key IS NOT NULL;\n            "
            : "";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE participants (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, kind TEXT NOT NULL);
            CREATE TABLE rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, room_id TEXT NOT NULL REFERENCES rooms(id),
                author_id TEXT NOT NULL REFERENCES participants(id), body TEXT NOT NULL, created_at TEXT NOT NULL{clientKeyColumn});
            CREATE INDEX ix_messages_room_id ON messages(room_id, id);
            {clientKeyIndex}CREATE TABLE read_cursors (participant_id TEXT NOT NULL REFERENCES participants(id),
                room_id TEXT NOT NULL REFERENCES rooms(id), last_read_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (participant_id, room_id));
            INSERT INTO participants (id, display_name, kind) VALUES
                ('owner','Owner','human'),('claude','Claude','model'),('codex','Codex','model');
            INSERT INTO rooms (id, name, created_at) VALUES
                {roomValues};
            PRAGMA user_version = {schemaVersion};
            """;
        cmd.ExecuteNonQuery();
    }

    private static string RoomName(string roomId) => roomId == "general" ? "General" : $"Room {roomId[5..]}";

    private static void InsertMessages(SqliteConnection conn, string[] roomIds, Dictionary<string, List<long>> roomMessageIds,
        DateTimeOffset baseTime, int startIndex, int count)
    {
        if (count == 0) return;
        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO messages (room_id, author_id, body, created_at) VALUES ($room, $author, $body, $at);
            SELECT last_insert_rowid();
            """;
        var pRoom = insert.Parameters.Add("$room", SqliteType.Text);
        var pAuthor = insert.Parameters.Add("$author", SqliteType.Text);
        var pBody = insert.Parameters.Add("$body", SqliteType.Text);
        var pAt = insert.Parameters.Add("$at", SqliteType.Text);

        for (int i = 0; i < count; i++)
        {
            int globalIndex = startIndex + i;
            var room = roomIds[globalIndex % roomIds.Length];
            var author = Participants[globalIndex % Participants.Length];
            pRoom.Value = room;
            pAuthor.Value = author;
            pBody.Value = $"Synthetic corpus message #{globalIndex} in {room} from {author}. Fabricated fixture text, not a real conversation.";
            pAt.Value = Stamp(baseTime.AddSeconds(globalIndex));
            long id = (long)insert.ExecuteScalar()!;
            roomMessageIds[room].Add(id);
        }
        tx.Commit();
    }

    /// <summary>Cursors "part-way through" each room: one distinct position per participant, all
    /// strictly before that room's last message, so a migration that clobbered a cursor is
    /// detectable rather than accidentally already-correct at the end of the room.</summary>
    private static void InsertCursors(SqliteConnection conn, string[] roomIds, Dictionary<string, List<long>> roomMessageIds)
    {
        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ($p, $r, $id)";
        var pParticipant = insert.Parameters.Add("$p", SqliteType.Text);
        var pRoom = insert.Parameters.Add("$r", SqliteType.Text);
        var pId = insert.Parameters.Add("$id", SqliteType.Integer);

        foreach (var room in roomIds)
        {
            var ids = roomMessageIds[room];
            if (ids.Count == 0) continue;
            for (int pi = 0; pi < Participants.Length; pi++)
            {
                int idx = Math.Clamp(ids.Count * (3 + pi) / 10, 0, ids.Count - 1);
                pParticipant.Value = Participants[pi];
                pRoom.Value = room;
                pId.Value = ids[idx];
                insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    private static Fingerprint ComputeFingerprint(SqliteConnection conn)
    {
        long count = Scalar(conn, "SELECT COUNT(*) FROM messages");
        long minId = count == 0 ? 0 : Scalar(conn, "SELECT MIN(id) FROM messages");
        long maxId = count == 0 ? 0 : Scalar(conn, "SELECT MAX(id) FROM messages");
        long bodyLen = Scalar(conn, "SELECT COALESCE(SUM(LENGTH(body)), 0) FROM messages");

        var perRoom = new SortedDictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT room_id, COUNT(*) FROM messages GROUP BY room_id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) perRoom[reader.GetString(0)] = reader.GetInt64(1);
        }

        var cursors = new SortedDictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT participant_id, room_id, last_read_id FROM read_cursors";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) cursors[$"{reader.GetString(0)}|{reader.GetString(1)}"] = reader.GetInt64(2);
        }

        return new Fingerprint(count, minId, maxId, bodyLen, perRoom, cursors);
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
