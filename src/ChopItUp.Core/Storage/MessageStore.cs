using ChopItUp.Core.Model;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

public sealed class MessageStore(ChopDb db)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public IReadOnlyList<Room> ListRooms()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.name, r.created_at, COALESCE(MAX(m.id), 0), COUNT(m.id)
            FROM rooms r LEFT JOIN messages m ON m.room_id = r.id
            GROUP BY r.id ORDER BY r.created_at, r.id
            """;
        using var reader = cmd.ExecuteReader();
        var rooms = new List<Room>();
        while (reader.Read())
            rooms.Add(new Room(reader.GetString(0), reader.GetString(1), Timestamps.Parse(reader.GetString(2)), reader.GetInt64(3), reader.GetInt32(4)));
        return rooms;
    }

    public bool RoomExists(string roomId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM rooms WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", roomId);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Exact count of messages with id greater than <paramref name="afterId"/>; never capped.</summary>
    public long CountAfter(string roomId, long afterId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE room_id = $room AND id > $after";
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$after", afterId);
        return (long)cmd.ExecuteScalar()!;
    }

    public const int MaxClientKeyChars = 200;

    /// <summary>Back-compatible overload: a post with no retry key always inserts.</summary>
    public Message Post(string roomId, string authorId, string body) => Post(roomId, authorId, body, null).Message;

    /// <summary>Appends a message and advances the author's own cursor past it (you have read what
    /// you wrote), in one transaction. Every posting path — MCP tools now, the M3 web UI later —
    /// goes through here so the rule cannot drift. With a <paramref name="clientKey"/> the write is
    /// idempotent: a repeat of the same key by the same author in the same room returns the stored
    /// message untouched. The unique index is the arbiter, not the pre-check, so two racing retries
    /// still collapse to one row.</summary>
    public PostResult Post(string roomId, string authorId, string body, string? clientKey)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("Message body is empty.", nameof(body));
        clientKey = string.IsNullOrWhiteSpace(clientKey) ? null : clientKey.Trim();
        if (clientKey is { Length: > MaxClientKeyChars })
            throw new ArgumentException($"client_key exceeds {MaxClientKeyChars} characters.", nameof(clientKey));

        var createdAt = DateTimeOffset.UtcNow;
        using var conn = db.Open();

        if (clientKey is not null && FindByClientKey(conn, roomId, authorId, clientKey) is { } already)
            return new PostResult(already, true);

        using var tx = conn.BeginTransaction();
        long id;
        try
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO messages (room_id, author_id, body, created_at, client_key) VALUES ($room, $author, $body, $at, $key);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$room", roomId);
            insert.Parameters.AddWithValue("$author", authorId);
            insert.Parameters.AddWithValue("$body", body);
            insert.Parameters.AddWithValue("$at", Timestamps.Stamp(createdAt));
            insert.Parameters.AddWithValue("$key", (object?)clientKey ?? DBNull.Value);
            id = (long)insert.ExecuteScalar()!;   // captured BEFORE the cursor upsert moves last_insert_rowid()
        }
        // 2067 is SQLITE_CONSTRAINT_UNIQUE. The bare code 19 is NOT usable here: a foreign-key
        // violation (unknown room or author) is also 19, and swallowing that would turn a real
        // integrity error into "the message could not be read back" (critique pass 1, F2).
        catch (SqliteException e) when (clientKey is not null && e.SqliteExtendedErrorCode == 2067)
        {
            tx.Rollback();
            var raced = FindByClientKey(conn, roomId, authorId, clientKey)
                ?? throw new InvalidOperationException("client_key collided but the stored message could not be read back.");
            return new PostResult(raced, true);
        }

        using var cursor = conn.CreateCommand();
        cursor.Transaction = tx;
        cursor.CommandText = """
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ($author, $room, $id)
            ON CONFLICT (participant_id, room_id) DO UPDATE SET last_read_id = MAX(last_read_id, excluded.last_read_id)
            """;
        cursor.Parameters.AddWithValue("$author", authorId);
        cursor.Parameters.AddWithValue("$room", roomId);
        cursor.Parameters.AddWithValue("$id", id);
        cursor.ExecuteNonQuery();

        tx.Commit();
        return new PostResult(new Message(id, roomId, authorId, body, createdAt), false);
    }

    private static Message? FindByClientKey(SqliteConnection conn, string roomId, string authorId, string clientKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, room_id, author_id, body, created_at FROM messages
            WHERE room_id = $room AND author_id = $author AND client_key = $key
            """;
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$author", authorId);
        cmd.Parameters.AddWithValue("$key", clientKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new Message(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Timestamps.Parse(reader.GetString(4)))
            : null;
    }

    /// <summary>How many messages were posted with and without a retry key, per author. The only
    /// evidence available that the hosts are using the mechanism the retry-safety claim rests on.</summary>
    public IReadOnlyList<(string AuthorId, long Keyed, long Keyless)> KeyUsage()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT author_id, SUM(client_key IS NOT NULL), SUM(client_key IS NULL)
            FROM messages GROUP BY author_id ORDER BY author_id
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string AuthorId, long Keyed, long Keyless)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        return rows;
    }

    public MessagePage Read(string roomId, long afterId, int limit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, room_id, author_id, body, created_at FROM messages
            WHERE room_id = $room AND id > $after ORDER BY id LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        using var reader = cmd.ExecuteReader();
        var rows = new List<Message>(limit + 1);
        while (reader.Read())
            rows.Add(new Message(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Timestamps.Parse(reader.GetString(4))));
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        long next = rows.Count == 0 ? afterId : rows[^1].Id;
        return new MessagePage(rows, next, hasMore);
    }

    public long GetCursor(string participantId, string roomId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_read_id FROM read_cursors WHERE participant_id = $p AND room_id = $r";
        cmd.Parameters.AddWithValue("$p", participantId);
        cmd.Parameters.AddWithValue("$r", roomId);
        return cmd.ExecuteScalar() is long v ? v : 0L;
    }

    /// <summary>Advances the cursor; never moves it backwards.</summary>
    public void SetCursor(string participantId, string roomId, long lastReadId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO read_cursors (participant_id, room_id, last_read_id) VALUES ($p, $r, $id)
            ON CONFLICT (participant_id, room_id) DO UPDATE SET last_read_id = MAX(last_read_id, excluded.last_read_id)
            """;
        cmd.Parameters.AddWithValue("$p", participantId);
        cmd.Parameters.AddWithValue("$r", roomId);
        cmd.Parameters.AddWithValue("$id", lastReadId);
        cmd.ExecuteNonQuery();
    }
}
