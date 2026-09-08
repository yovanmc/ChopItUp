using ChopItUp.Core.Memory;
using ChopItUp.Core.Model;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

/// <summary>Proposals are rows, not files (plan decision 6): pending ones survive a restart and never
/// churn the git-backed store. Shape is validated here with the same rules the store applies on
/// approval, so an approved proposal can always be written.</summary>
public sealed class MemoryProposalStore(ChopDb db)
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;
    public const string KindAppend = "append";
    public const string KindSupersede = "supersede";

    public MemoryProposal Create(string roomId, string authorId, string topic, string title, string body, string? source, string? replaces = null, string? flags = null)
    {
        MemoryStore.RequireSlug(topic);
        MemoryStore.Validate(title, body);
        replaces = replaces?.Trim();
        var kind = replaces is null ? KindAppend : KindSupersede;
        var at = DateTimeOffset.UtcNow;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_proposals (room_id, author_id, topic, title, body, status, source, created_at, kind, replaces, flags)
            VALUES ($room, $author, $topic, $title, $body, 'pending', $source, $at, $kind, $replaces, $flags);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$author", authorId);
        cmd.Parameters.AddWithValue("$topic", topic);
        cmd.Parameters.AddWithValue("$title", title.Trim());
        cmd.Parameters.AddWithValue("$body", body.Trim());
        cmd.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(at));
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$replaces", (object?)replaces ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$flags", (object?)flags ?? DBNull.Value);
        var id = (long)cmd.ExecuteScalar()!;
        return new MemoryProposal(id, roomId, authorId, topic, title.Trim(), body.Trim(), Pending, source, at, null, null, null, kind, replaces, flags);
    }

    public MemoryProposal? Get(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>The panel's default: everything that still needs the owner — pending, plus approved rows
    /// whose write never landed (plan decision 17). Not a status value; a predicate.</summary>
    public const string Undecided = "undecided";

    /// <summary>Ascending by id. A null <paramref name="roomId"/> or <paramref name="status"/> means any;
    /// <see cref="Undecided"/> means pending or approved-but-unwritten.</summary>
    public IReadOnlyList<MemoryProposal> List(string? roomId = null, string? status = Pending, int limit = DefaultLimit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE ($room IS NULL OR room_id = $room) AND ($status IS NULL OR status = $status OR ($status = 'undecided' AND (status = 'pending' OR (status = 'approved' AND written_to IS NULL)))) ORDER BY id LIMIT $limit";
        cmd.Parameters.AddWithValue("$room", (object?)roomId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var rows = new List<MemoryProposal>();
        while (reader.Read()) rows.Add(Map(reader));
        return rows;
    }

    /// <summary>The import idempotency key is author + topic + title over pending and approved rows; a
    /// rejected one does not count, so a wrong folder rejected row by row cannot poison the right
    /// folder (critique pass 1, P1-7).</summary>
    public bool Exists(string authorId, string topic, string title)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_proposals WHERE author_id = $author AND topic = $topic AND title = $title AND status <> 'rejected'";
        cmd.Parameters.AddWithValue("$author", authorId);
        cmd.Parameters.AddWithValue("$topic", topic);
        cmd.Parameters.AddWithValue("$title", title.Trim());
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Moves a PENDING proposal to <paramref name="status"/>; null when it was not pending or
    /// does not exist. The WHERE is the arbiter, so two racing decisions collapse to one.</summary>
    public MemoryProposal? Decide(long id, string status, string? writtenTo, string? commitHash)
    {
        if (status is not (Approved or Rejected)) throw new ArgumentException("status must be approved or rejected.", nameof(status));
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memory_proposals SET status = $status, decided_at = $at, written_to = $written, commit_hash = $hash
            WHERE id = $id AND status = 'pending'
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$written", (object?)writtenTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", (object?)commitHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1 ? Get(id) : null;
    }

    /// <summary>Records where an APPROVED proposal was written and the commit that holds it; null when
    /// the row is not approved. Separate from <see cref="Decide"/> so the row is marked before the file
    /// is written and a crash in between leaves a replayable approved-but-unwritten row (plan decision 15).</summary>
    public MemoryProposal? RecordWrite(long id, string writtenTo, string? commitHash)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memory_proposals SET written_to = $written, commit_hash = $hash WHERE id = $id AND status = 'approved'";
        cmd.Parameters.AddWithValue("$written", writtenTo);
        cmd.Parameters.AddWithValue("$hash", (object?)commitHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1 ? Get(id) : null;
    }

    /// <summary>Discards every PENDING proposal of one import (plan decision 16). Returns the count.</summary>
    public int DeletePending(string source)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memory_proposals WHERE source = $source AND status = 'pending'";
        cmd.Parameters.AddWithValue("$source", source);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Row 18, decision 5: the oldest PENDING proposal with this topic + title by ANY author,
    /// or null — pending only, so a newer approved row can never mask it (critique P1-17).
    /// <see cref="Exists"/> stays author-keyed for the import path.</summary>
    public MemoryProposal? FindPending(string topic, string title)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE topic = $topic AND title = $title AND status = 'pending' ORDER BY id LIMIT 1";
        cmd.Parameters.AddWithValue("$topic", topic);
        cmd.Parameters.AddWithValue("$title", title.Trim());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private const string Select = "SELECT id, room_id, author_id, topic, title, body, status, source, created_at, decided_at, written_to, commit_hash, kind, replaces, flags FROM memory_proposals";

    private static MemoryProposal Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        Timestamps.Parse(r.GetString(8)),
        r.IsDBNull(9) ? null : Timestamps.Parse(r.GetString(9)),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11),
        r.GetString(12),
        r.IsDBNull(13) ? null : r.GetString(13),
        r.IsDBNull(14) ? null : r.GetString(14));
}
