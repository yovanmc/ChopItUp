using ChopItUp.Core.Model;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

/// <summary>M25 task 4: the same "agent proposes, owner approves" shape <see cref="MemoryProposalStore"/>
/// already carries, repeated over <c>skill_proposals</c> (schema v10) for skill imports proposed
/// through the hub's own MCP surface rather than the CLI. Same status vocabulary as memory
/// (<see cref="Pending"/>/<see cref="Approved"/>/<see cref="Rejected"/>, and <see cref="Undecided"/> as
/// a predicate rather than a stored value) so the two panels read alike — including memory's
/// definition of <see cref="Undecided"/> as pending union approved-with-nothing-written
/// (<c>MemoryProposalStore.cs:80-84</c>), which here means pending union
/// approved-with-<c>installed_at</c>-NULL: what lets task 7's Retry arm find and finish a row whose
/// install was marked approved but never recorded as installed.</summary>
public sealed class SkillProposalStore(ChopDb db)
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    /// <summary>The panel's default: everything that still needs the owner — pending, plus approved
    /// rows whose install never landed. Not a status value; a predicate (mirrors
    /// <see cref="MemoryProposalStore.Undecided"/>).</summary>
    public const string Undecided = "undecided";

    /// <summary>Records a new pending proposal. <paramref name="treeSha256"/> is
    /// <c>SkillImport.ManifestDigest(SkillImport.HashSourceTree(sourceDir))</c> at call time (D5);
    /// <paramref name="replacesInstalled"/> and <paramref name="force"/> are the tool argument and the
    /// installed-state snapshot the card will show, captured now rather than derived later (plan pass
    /// 2 blocker 2); <paramref name="files"/> and <paramref name="bytes"/> are what the listing shows
    /// without re-walking the tree on every unauthenticated GET.</summary>
    public SkillProposal Add(string roomId, string authorId, string name, string sourceDir, string treeSha256,
        bool replacesInstalled, bool force, int files, long bytes)
    {
        var at = DateTimeOffset.UtcNow;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO skill_proposals (room_id, author_id, name, source_dir, tree_sha256, replaces_installed, force, files, bytes, status, created_at)
            VALUES ($room, $author, $name, $source, $tree, $replaces, $force, $files, $bytes, 'pending', $at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$author", authorId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$source", sourceDir);
        cmd.Parameters.AddWithValue("$tree", treeSha256);
        cmd.Parameters.AddWithValue("$replaces", replacesInstalled ? 1 : 0);
        cmd.Parameters.AddWithValue("$force", force ? 1 : 0);
        cmd.Parameters.AddWithValue("$files", files);
        cmd.Parameters.AddWithValue("$bytes", bytes);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(at));
        var id = (long)cmd.ExecuteScalar()!;
        return new SkillProposal(id, roomId, authorId, name, sourceDir, treeSha256, replacesInstalled, force, files, bytes, Pending, at, null, null);
    }

    public SkillProposal? Get(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Ascending by id. A null <paramref name="roomId"/> or <paramref name="status"/> means
    /// any; <see cref="Undecided"/> means pending or approved-but-not-yet-installed.</summary>
    public IReadOnlyList<SkillProposal> List(string? roomId = null, string? status = Pending, int limit = DefaultLimit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + """
             WHERE ($room IS NULL OR room_id = $room)
               AND ($status IS NULL OR status = $status OR ($status = 'undecided' AND (status = 'pending' OR (status = 'approved' AND installed_at IS NULL))))
             ORDER BY id LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$room", (object?)roomId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var rows = new List<SkillProposal>();
        while (reader.Read()) rows.Add(Map(reader));
        return rows;
    }

    /// <summary>AC3's dedup: the oldest UNDECIDED proposal for this exact skill name and tree hash, or
    /// null — pending or approved-but-not-yet-installed, so a proposal the owner already decided
    /// (rejected, or approved and finished) never blocks a fresh offer of the same tree, and a
    /// duplicate offer of a still-live one returns the existing card instead of minting a second.</summary>
    public SkillProposal? FindPending(string name, string treeSha256)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + """
             WHERE name = $name AND tree_sha256 = $tree
               AND (status = 'pending' OR (status = 'approved' AND installed_at IS NULL))
             ORDER BY id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$tree", treeSha256);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Moves a PENDING row to approved and stamps <c>decided_at</c>; null when the row was
    /// not pending (already decided, or unknown) — the WHERE is the arbiter, so a repeat call is a
    /// no-op rather than overwriting the stamp, and two racing decisions collapse to one.</summary>
    public SkillProposal? MarkApproved(long id) => Decide(id, Approved);

    /// <summary>Moves a PENDING row to rejected and stamps <c>decided_at</c>; null when the row was
    /// not pending. Same no-op-on-repeat contract as <see cref="MarkApproved"/>.</summary>
    public SkillProposal? MarkRejected(long id) => Decide(id, Rejected);

    private SkillProposal? Decide(long id, string status)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE skill_proposals SET status = $status, decided_at = $at WHERE id = $id AND status = 'pending'";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1 ? Get(id) : null;
    }

    /// <summary>Stamps <c>installed_at</c> on an APPROVED row whose install has not been recorded yet;
    /// null when the row is not approved or was already marked installed. The WHERE guards both halves
    /// (status AND installed_at IS NULL), so a repeat call — task 7's Retry arm calling this a second
    /// time after an interrupted approval — is a no-op rather than overwriting the original stamp.</summary>
    public SkillProposal? MarkInstalled(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE skill_proposals SET installed_at = $at WHERE id = $id AND status = 'approved' AND installed_at IS NULL";
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1 ? Get(id) : null;
    }

    private const string Select = "SELECT id, room_id, author_id, name, source_dir, tree_sha256, replaces_installed, force, files, bytes, status, created_at, decided_at, installed_at FROM skill_proposals";

    private static SkillProposal Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetInt64(6) != 0, r.GetInt64(7) != 0, (int)r.GetInt64(8), r.GetInt64(9), r.GetString(10),
        Timestamps.Parse(r.GetString(11)),
        r.IsDBNull(12) ? null : Timestamps.Parse(r.GetString(12)),
        r.IsDBNull(13) ? null : Timestamps.Parse(r.GetString(13)));
}
