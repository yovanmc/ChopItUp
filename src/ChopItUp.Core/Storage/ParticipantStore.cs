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
        cmd.CommandText = "SELECT id, display_name, kind, host, model, note FROM participants ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var rows = new List<Participant>();
        while (reader.Read())
            rows.Add(new Participant(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    /// <summary>The one human. The schema allows more than one row of kind 'human'; the hub does not
    /// (grill: "the owner is the only human here"), so this throws rather than picking one.</summary>
    public string HumanId()
    {
        var humans = List().Where(p => p.Kind == "human").Select(p => p.Id).ToArray();
        return humans.Length == 1
            ? humans[0]
            : throw new InvalidOperationException($"Expected exactly one participant of kind 'human', found {humans.Length}.");
    }
}
