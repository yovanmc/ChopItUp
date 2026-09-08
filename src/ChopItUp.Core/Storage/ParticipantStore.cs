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
        cmd.CommandText = "SELECT id, display_name, kind, host, model, note, classes FROM participants ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var rows = new List<Participant>();
        while (reader.Read())
            rows.Add(new Participant(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
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
}
