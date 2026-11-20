using ChopItUp.Core.Model;

namespace ChopItUp.Core.Storage;

/// <summary>The <c>jobs</c> table (schema v18). A job row is written before anything runs and ended
/// exactly once, so a restart can tell which jobs a previous hub process lost.</summary>
public sealed class JobStore(ChopDb db)
{
    private const string Columns = "id, room_id, run_id, author_id, command, cancel_command, gpu, timeout_minutes, folder, status, exit_code, pid, process_started_at, started_at, ended_at, noted_at";

    /// <summary>Inserts a running job and returns its id. <paramref name="folderFor"/> turns the new
    /// id into the job's folder, so the folder can carry the id.</summary>
    public Job Start(string roomId, long runId, string authorId, string command, string? cancelCommand, bool gpu,
        int timeoutMinutes, Func<long, string> folderFor, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO jobs (room_id, run_id, author_id, command, cancel_command, gpu, timeout_minutes, folder, status, started_at)
            VALUES ($room, $run, $author, $command, $cancel, $gpu, $timeout, '', $status, $at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$author", authorId);
        cmd.Parameters.AddWithValue("$command", command);
        cmd.Parameters.AddWithValue("$cancel", (object?)cancelCommand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gpu", gpu ? 1 : 0);
        cmd.Parameters.AddWithValue("$timeout", timeoutMinutes);
        cmd.Parameters.AddWithValue("$status", JobStatus.Running);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(now));
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        cmd.Parameters.Clear();
        cmd.CommandText = "UPDATE jobs SET folder = $folder WHERE id = $id";
        cmd.Parameters.AddWithValue("$folder", folderFor(id));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return Get(id)!;
    }

    /// <summary>Records that the job's process started.</summary>
    public void MarkProcessStarted(long id, int pid, DateTimeOffset now) =>
        Exec("UPDATE jobs SET pid = $pid, process_started_at = $at WHERE id = $id",
            ("$pid", pid), ("$at", Timestamps.Stamp(now)), ("$id", id));

    /// <summary>Ends a running job. False when it had already ended, which changes nothing.</summary>
    public bool End(long id, string status, int? exitCode, DateTimeOffset now) =>
        Exec("UPDATE jobs SET status = $status, exit_code = $exit, ended_at = $at WHERE id = $id AND status = $running",
            ("$status", status), ("$exit", (object?)exitCode ?? DBNull.Value), ("$at", Timestamps.Stamp(now)),
            ("$id", id), ("$running", JobStatus.Running)) == 1;

    /// <summary>Records that the job's end note reached its room.</summary>
    public void MarkNoted(long id, DateTimeOffset now) =>
        Exec("UPDATE jobs SET noted_at = $at WHERE id = $id AND noted_at IS NULL", ("$at", Timestamps.Stamp(now)), ("$id", id));

    public Job? Get(long id) => Query("WHERE id = $id", ("$id", id)).SingleOrDefault();

    /// <summary>Who started the ended job whose folder is named <paramref name="folderName"/>, or null
    /// while it runs or when no job has that folder. Folder names carry a random suffix, so a name
    /// names one job across every run and every hub that shares the database.</summary>
    public string? EndedAuthorOfFolder(string folderName)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT author_id FROM jobs
            WHERE status <> $running AND length(folder) > length($name)
              AND substr(folder, -length($name) - 1) IN ('\' || $name, '/' || $name)
            """;
        cmd.Parameters.AddWithValue("$running", JobStatus.Running);
        cmd.Parameters.AddWithValue("$name", folderName);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Every job still running, plus every ended job whose end note never reached its room:
    /// what a starting hub must report.</summary>
    public IReadOnlyList<Job> Unreported() =>
        Query("WHERE status = $running OR noted_at IS NULL ORDER BY id", ("$running", JobStatus.Running));

    private int Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    private List<Job> Query(string where, params (string Name, object Value)[] parameters)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM jobs {where}";
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        var jobs = new List<Job>();
        while (r.Read())
        {
            DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : Timestamps.Parse(r.GetString(i));
            jobs.Add(new Job(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6) != 0, r.GetInt32(7), r.GetString(8), r.GetString(9),
                r.IsDBNull(10) ? null : r.GetInt32(10), r.IsDBNull(11) ? null : r.GetInt32(11), At(12),
                Timestamps.Parse(r.GetString(13)), At(14), At(15)));
        }
        return jobs;
    }
}
