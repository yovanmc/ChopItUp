using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

/// <summary>What the hub knows when it launches a spawn. <see cref="RequestedModel"/> and
/// <see cref="Effort"/> are what the hub passed the CLI (a null effort means no flag was passed);
/// <see cref="Retry"/> is true for the relaunch a usage-limit requeue scheduled.</summary>
public sealed record TaskStart(string SpawnId, string RoomId, string ParticipantId, string Host, string? RequestedModel, string? Effort,
    long RootMessageId, int Turn, long? RunId, bool Retry, DateTimeOffset StartedAt);

/// <summary>What the hub knows when a spawn finishes. <see cref="ToolCalls"/> is null when the
/// spawn's output does not list its calls; <see cref="Usage"/> is the reported usage block, or null.</summary>
public sealed record TaskEnd(DateTimeOffset EndedAt, long DurationMs, int? ExitCode, string Termination, int? ToolCalls, int Posts, string? Usage);

/// <summary>One spawn's telemetry row. <see cref="End"/> is null until the spawn finishes, and stays
/// null for a spawn the hub stopped or crashed under.</summary>
public sealed record TaskRecord(TaskStart Start, TaskEnd? End);

/// <summary>The <c>spawn_tasks</c> table: one row per spawn launch, written at launch and completed
/// once at finish.</summary>
public sealed class TaskStore(ChopDb db)
{
    /// <summary>Inserts the launch row. False when a row with that spawn id exists; it is never
    /// overwritten.</summary>
    public bool Start(TaskStart t)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO spawn_tasks (spawn_id, room_id, participant_id, host, requested_model, effort, root_message_id, turn, run_id, retry, started_at)
            VALUES ($id, $room, $participant, $host, $model, $effort, $root, $turn, $run, $retry, $at)
            """;
        cmd.Parameters.AddWithValue("$id", t.SpawnId);
        cmd.Parameters.AddWithValue("$room", t.RoomId);
        cmd.Parameters.AddWithValue("$participant", t.ParticipantId);
        cmd.Parameters.AddWithValue("$host", t.Host);
        cmd.Parameters.AddWithValue("$model", (object?)t.RequestedModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$effort", (object?)t.Effort ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$root", t.RootMessageId);
        cmd.Parameters.AddWithValue("$turn", t.Turn);
        cmd.Parameters.AddWithValue("$run", (object?)t.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$retry", t.Retry ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(t.StartedAt));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Completes an open row. False when no open row has that id (unknown, or already
    /// finished).</summary>
    public bool Finish(string spawnId, TaskEnd e)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE spawn_tasks SET ended_at = $at, duration_ms = $ms, exit_code = $exit, termination = $term, tool_calls = $tools, posts = $posts, usage = $usage
            WHERE spawn_id = $id AND ended_at IS NULL
            """;
        cmd.Parameters.AddWithValue("$id", spawnId);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(e.EndedAt));
        cmd.Parameters.AddWithValue("$ms", e.DurationMs);
        cmd.Parameters.AddWithValue("$exit", (object?)e.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$term", e.Termination);
        cmd.Parameters.AddWithValue("$tools", (object?)e.ToolCalls ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$posts", e.Posts);
        cmd.Parameters.AddWithValue("$usage", (object?)e.Usage ?? DBNull.Value);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>A room's tasks, newest launch first; spawns launched in one pass share an instant and
    /// list in reverse launch order.</summary>
    public IReadOnlyList<TaskRecord> ForRoom(string roomId, int limit)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT spawn_id, room_id, participant_id, host, requested_model, effort, root_message_id, turn, run_id, retry, started_at,
                   ended_at, duration_ms, exit_code, termination, tool_calls, posts, usage
            FROM spawn_tasks WHERE room_id = $room ORDER BY started_at DESC, rowid DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<TaskRecord>();
        while (r.Read())
        {
            var start = new TaskStart(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Text(r, 4), Text(r, 5),
                r.GetInt64(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.GetInt64(9) != 0, Timestamps.Parse(r.GetString(10)));
            var end = r.IsDBNull(11) ? null : new TaskEnd(Timestamps.Parse(r.GetString(11)), r.GetInt64(12), r.IsDBNull(13) ? null : r.GetInt32(13),
                r.GetString(14), r.IsDBNull(15) ? null : r.GetInt32(15), r.GetInt32(16), Text(r, 17));
            list.Add(new TaskRecord(start, end));
        }
        return list;
    }

    /// <summary>How many messages carry <paramref name="spawnId"/>: the posts of a spawn whose finish
    /// never ran.</summary>
    public int StampedPosts(string spawnId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE spawn_id = $id";
        cmd.Parameters.AddWithValue("$id", spawnId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
