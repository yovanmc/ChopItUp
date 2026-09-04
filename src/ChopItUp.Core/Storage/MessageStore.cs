using System.Globalization;
using ChopItUp.Core.Model;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

public sealed class MessageStore(ChopDb db)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private static string Stamp(DateTimeOffset at) => Timestamps.Stamp(at);
    private static DateTimeOffset Parse(string stamp) => Timestamps.Parse(stamp);

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
            rooms.Add(new Room(reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)), reader.GetInt64(3), reader.GetInt32(4)));
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

    /// <summary>Appends a message and advances the author's own cursor past it (you have read what
    /// you wrote), in one transaction. Every posting path — MCP tools now, the M3 web UI later —
    /// goes through here so the rule cannot drift.</summary>
    public Message Post(string roomId, string authorId, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("Message body is empty.", nameof(body));
        var createdAt = DateTimeOffset.UtcNow;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO messages (room_id, author_id, body, created_at) VALUES ($room, $author, $body, $at);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$room", roomId);
        insert.Parameters.AddWithValue("$author", authorId);
        insert.Parameters.AddWithValue("$body", body);
        insert.Parameters.AddWithValue("$at", Stamp(createdAt));
        long id = (long)insert.ExecuteScalar()!;   // captured BEFORE the cursor upsert moves last_insert_rowid()

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
        return new Message(id, roomId, authorId, body, createdAt);
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
            rows.Add(new Message(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4))));
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
