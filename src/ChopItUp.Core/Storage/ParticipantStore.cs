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
        cmd.CommandText = "SELECT id, display_name, kind, host, model, note, classes, role FROM participants ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var rows = new List<Participant>();
        while (reader.Read())
            rows.Add(new Participant(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        return rows;
    }

    /// <summary>The owner's row. Kind 'human' stopped being unique at v7 (the remote proxy, grill
    /// ledger D3), so this is an id lookup, not a kind filter: `owner` is the identity every
    /// owner-scoped read resolves to — unread counts, the web UI's post author, the trail identity —
    /// while `owner-remote` is a second hand on the same authority, distinguished in the transcript.
    /// Throws when the row is absent, which no migrated or fresh database can be.</summary>
    public string OwnerId() =>
        List().Any(p => p.Id == ChopDb.OwnerParticipantId)
            ? ChopDb.OwnerParticipantId
            : throw new InvalidOperationException($"The roster has no '{ChopDb.OwnerParticipantId}' row; this database was not created or migrated by this build.");

    /// <summary>Every row that may speak with the owner's authority (kind 'human'). The exchange
    /// policy keys on kind, not on this list; this is for prose and diagnostics.</summary>
    public IReadOnlyList<string> HumanIds() =>
        List().Where(p => p.Kind == "human").Select(p => p.Id).ToList();

    /// <summary>Row 20 task 2: persists a class set on one roster row (a Codex row included), the
    /// host command's only write path — normal service never assigns classes. <paramref name="classes"/>
    /// is normalized through <see cref="ParticipantClasses.Parse"/> before it is stored: unrecognised
    /// tokens are dropped, known ones deduplicated and reordered to <see cref="ParticipantClasses.All"/>'s
    /// order, and an empty or whitespace input clears the row (stored as NULL, the same as a row that
    /// never had a class). Returns whether the row existed (exactly one row updated); it never
    /// inserts, so an unknown id changes nothing and returns false.</summary>
    public bool SetClasses(string id, string classes)
    {
        var normalized = ParticipantClasses.Parse(classes);
        var stored = normalized.Count == 0 ? null : string.Join(",", normalized);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE participants SET classes = @classes WHERE id = @id";
        cmd.Parameters.AddWithValue("@classes", (object?)stored ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Row 14 (D-h): the cap on a global role and on a room override, enforced here so a
    /// future writer that is not the HTTP API cannot exceed it. Equals <see cref="ChopItUp.Core.Memory.MemoryStore.RoomChars"/>
    /// so the two per-room text budgets match.</summary>
    public const int MaxRoleChars = 2000;

    /// <summary>Sets a participant's global role (row 14). The WHERE clause is the spawnability
    /// arbiter — <c>kind = 'model' AND model IS NOT NULL</c>, never <c>kind = 'model'</c> alone
    /// (D-g): <c>claude</c> and <c>codex</c> are kind 'model' with a NULL model, app-backed windows
    /// the hub never spawns, so a role stored there could never render. A non-spawnable or unknown id
    /// changes nothing and returns false without a second query. An empty or whitespace
    /// <paramref name="role"/> clears it to NULL.</summary>
    public bool SetRole(string id, string? role)
    {
        var trimmed = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
        if (trimmed is { Length: > MaxRoleChars })
            throw new ArgumentException($"Role exceeds {MaxRoleChars} characters.", nameof(role));
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE participants SET role = @role WHERE id = @id AND kind = 'model' AND model IS NOT NULL";
        cmd.Parameters.AddWithValue("@role", (object?)trimmed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Removes this room's override for a participant (row 14), falling back to the global
    /// role. Returns true when the room and a spawnable participant both exist, even when no override
    /// row was there to delete — clearing an absent override is a success, not a failure (LESSON M29's
    /// "nothing to guard" arm).</summary>
    public bool ClearRoomRole(string roomId, string id)
    {
        using var conn = db.Open();
        if (!RoomAndSpawnableParticipantExist(conn, roomId, id)) return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM room_roles WHERE room_id = @room AND participant_id = @id";
        cmd.Parameters.AddWithValue("@room", roomId);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        return true;
    }

    /// <summary>Stores this room's override for a participant (row 14), upserting rather than
    /// throwing on a second write. <paramref name="role"/> may be the empty string — D-b's "no role in
    /// this room" sentinel, a stored row distinct from having no override at all — and it must NOT be
    /// turned into a delete; use <see cref="ClearRoomRole"/> for that. Returns false when the room or
    /// a spawnable participant does not exist; <c>ChopDb.Open</c> enforces the foreign keys regardless,
    /// so this existence check exists to produce a clean refusal, not to guard integrity.</summary>
    public bool SetRoomRole(string roomId, string id, string role)
    {
        if (role is { Length: > MaxRoleChars })
            throw new ArgumentException($"Role exceeds {MaxRoleChars} characters.", nameof(role));
        using var conn = db.Open();
        if (!RoomAndSpawnableParticipantExist(conn, roomId, id)) return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO room_roles (room_id, participant_id, role) VALUES (@room, @id, @role)
            ON CONFLICT (room_id, participant_id) DO UPDATE SET role = excluded.role
            """;
        cmd.Parameters.AddWithValue("@room", roomId);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.ExecuteNonQuery();
        return true;
    }

    private static bool RoomAndSpawnableParticipantExist(SqliteConnection conn, string roomId, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT 1 FROM rooms WHERE id = @room),
                   (SELECT 1 FROM participants WHERE id = @id AND kind = 'model' AND model IS NOT NULL)
            """;
        cmd.Parameters.AddWithValue("@room", roomId);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return !reader.IsDBNull(0) && !reader.IsDBNull(1);
    }

    /// <summary>This participant's global role, or null. Row 14.</summary>
    public string? GlobalRole(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT role FROM participants WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is string s ? s : null;
    }

    /// <summary>This room's override for this participant, or null when there is none stored (which
    /// is different from a stored empty string — D-b's "no role in this room" sentinel). Row 14.</summary>
    public string? RoomRole(string roomId, string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT role FROM room_roles WHERE room_id = @room AND participant_id = @id";
        cmd.Parameters.AddWithValue("@room", roomId);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is string s ? s : null;
    }

    /// <summary>The role actually rendered into this participant's spawn prompt in this room: the
    /// room's override when one is stored (D-b — including the empty-string sentinel), else the
    /// global role, else null. This is the deciding function for AC3 (LESSON M24: tested directly).</summary>
    public string? EffectiveRole(string roomId, string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(
                (SELECT role FROM room_roles WHERE room_id = @room AND participant_id = @id),
                (SELECT role FROM participants WHERE id = @id))
            """;
        cmd.Parameters.AddWithValue("@room", roomId);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is string s ? s : null;
    }

    /// <summary>Every override stored in one room, for the Roles dialog. Row 14.</summary>
    public IReadOnlyDictionary<string, string> RoomRoles(string roomId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT participant_id, role FROM room_roles WHERE room_id = @room";
        cmd.Parameters.AddWithValue("@room", roomId);
        using var reader = cmd.ExecuteReader();
        var rows = new Dictionary<string, string>();
        while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }
}
