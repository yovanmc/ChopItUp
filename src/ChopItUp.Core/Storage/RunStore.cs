using ChopItUp.Core.Model;
using Microsoft.Data.Sqlite;

namespace ChopItUp.Core.Storage;

/// <summary>The <c>runs</c> table and its three satellites: the only hub state that outlives an
/// exchange. Every mutating member takes the caller's <c>now</c>, including <see cref="Start"/>, so a
/// run's whole timeline can be driven by a fake clock in tests.</summary>
public sealed class RunStore(ChopDb db)
{
    private const string Select = """
        SELECT id, room_id, conductor_id, skill_name, arguments, status, reason, cap_spent, phase,
               root_message_id, started_at, parked_at, parked_seconds, ended_at, spawns_used, exchanges,
               budget_hours, spawn_budget, waiting_since, waited_seconds
        FROM runs
        """;

    /// <summary>Inserts an <c>active</c> run. <c>ux_runs_one_active_per_room</c> is the arbiter for a
    /// second run in the same room, surfaced as <see cref="InvalidOperationException"/> — never
    /// confused with a foreign-key failure on a bad room or participant id, which propagates as the
    /// raw <see cref="SqliteException"/> it is.</summary>
    public Run Start(string roomId, string conductorId, string skillName, string arguments, long rootMessageId,
        int budgetHours, int spawnBudget, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs (room_id, conductor_id, skill_name, arguments, status, root_message_id, started_at, budget_hours, spawn_budget)
            VALUES ($room, $conductor, $skill, $args, 'active', $root, $at, $hours, $spawns);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$conductor", conductorId);
        cmd.Parameters.AddWithValue("$skill", skillName);
        cmd.Parameters.AddWithValue("$args", arguments);
        cmd.Parameters.AddWithValue("$root", rootMessageId);
        cmd.Parameters.AddWithValue("$hours", budgetHours);
        cmd.Parameters.AddWithValue("$spawns", spawnBudget);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(now));
        long id;
        try { id = (long)cmd.ExecuteScalar()!; }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == 2067)   // SQLITE_CONSTRAINT_UNIQUE: ux_runs_one_active_per_room
        {
            throw new InvalidOperationException($"Room '{roomId}' already has an active run.", e);
        }
        return ById(id)!;
    }

    public Run? ById(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>The room's one <c>active</c> run, or null. Unique by construction.</summary>
    public Run? Active(string roomId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE room_id = $room AND status = 'active'";
        cmd.Parameters.AddWithValue("$room", roomId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>The room's most recent run of any status, or null when the room has never had one.</summary>
    public Run? Latest(string roomId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE room_id = $room ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$room", roomId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Run> ListActive()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Select + " WHERE status = 'active' ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<Run>();
        while (reader.Read()) rows.Add(Map(reader));
        return rows;
    }

    /// <summary>Stops the run for a recoverable reason and stamps <c>parked_at</c>, so
    /// <see cref="Resume"/> can later add the parked interval to <c>parked_seconds</c>.
    /// <paramref name="capSpent"/> is true only for a hard cap (spawns, wall clock, phase re-entry),
    /// false for a refusal or silence park, whoever reached it. An open wait is folded into
    /// <c>waited_seconds</c> at <paramref name="now"/>, so the restart park counts hub downtime during a
    /// wait as waited time.</summary>
    public Run Park(long id, string reason, bool capSpent, DateTimeOffset now)
    {
        var fold = OpenWaitSeconds(id, now);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET status = 'parked', reason = $reason, cap_spent = $cap, parked_at = $at,
                            waiting_since = NULL, waited_seconds = waited_seconds + $fold
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$cap", capSpent ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(now));
        cmd.Parameters.AddWithValue("$fold", fold);
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Run #{id} does not exist.");
        return ById(id)!;
    }

    /// <summary>A parked run's budget becomes <paramref name="budgetHours"/> and
    /// <paramref name="spawnBudget"/>, its phase-entry counts reset to none, its cap-spent flag
    /// cleared, and it resumes (the parked interval folds as in <see cref="Resume"/>). One
    /// transaction. Throws when the run is not parked or the budget would shrink.</summary>
    public Run Extend(long id, int budgetHours, int spawnBudget, DateTimeOffset now)
    {
        var current = ById(id) ?? throw new InvalidOperationException($"Run #{id} does not exist.");
        if (current.Status != RunStatus.Parked)
            throw new InvalidOperationException($"Run #{id} is {current.Status}, not parked.");
        if (budgetHours < current.BudgetHours || spawnBudget < current.SpawnBudget)
            throw new InvalidOperationException($"Run #{id}: an extension never shrinks the budget.");
        var addedSeconds = (long)Math.Max(0, (now - (current.ParkedAt ?? now)).TotalSeconds);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM run_phases WHERE run_id = $id;
            UPDATE runs SET status = 'active', reason = NULL, cap_spent = 0, parked_at = NULL,
                            parked_seconds = parked_seconds + $added,
                            budget_hours = $hours, spawn_budget = $spawns
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$added", addedSeconds);
        cmd.Parameters.AddWithValue("$hours", budgetHours);
        cmd.Parameters.AddWithValue("$spawns", spawnBudget);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return ById(id)!;
    }

    /// <summary>Marks an active run as waiting on a job from <paramref name="now"/>. A run already
    /// waiting keeps its first instant.</summary>
    public Run StartWait(long id, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE runs SET waiting_since = $at WHERE id = $id AND status = 'active' AND waiting_since IS NULL";
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(now));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return ById(id) ?? throw new InvalidOperationException($"Run #{id} does not exist.");
    }

    /// <summary>Ends an open wait: the interval up to <paramref name="now"/> joins
    /// <c>waited_seconds</c>. A run that is not waiting is returned unchanged.</summary>
    public Run EndWait(long id, DateTimeOffset now)
    {
        var fold = OpenWaitSeconds(id, now);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE runs SET waiting_since = NULL, waited_seconds = waited_seconds + $fold WHERE id = $id AND waiting_since IS NOT NULL";
        cmd.Parameters.AddWithValue("$fold", fold);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return ById(id) ?? throw new InvalidOperationException($"Run #{id} does not exist.");
    }

    private long OpenWaitSeconds(long id, DateTimeOffset now) =>
        ById(id)?.WaitingSince is { } since ? (long)Math.Max(0, (now - since).TotalSeconds) : 0;

    /// <summary>Parked -&gt; active: clears the reason, adds the whole parked interval to
    /// <c>parked_seconds</c> and clears <c>parked_at</c>. Throws when the run is not parked, or is
    /// parked with a spent hard cap, which is not resumable.</summary>
    public Run Resume(long id, DateTimeOffset now)
    {
        var current = ById(id) ?? throw new InvalidOperationException($"Run #{id} does not exist.");
        if (current.Status != RunStatus.Parked)
            throw new InvalidOperationException($"Run #{id} is {current.Status}, not parked.");
        if (current.CapSpent)
            throw new InvalidOperationException($"Run #{id} is parked because a hard cap is spent and cannot be resumed.");

        var parkedAt = current.ParkedAt ?? now;
        var addedSeconds = (long)Math.Max(0, (now - parkedAt).TotalSeconds);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET status = 'active', reason = NULL, parked_at = NULL, parked_seconds = parked_seconds + $added
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$added", addedSeconds);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return ById(id)!;
    }

    public Run End(long id, string reason, DateTimeOffset now)
    {
        var fold = OpenWaitSeconds(id, now);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE runs SET status = 'ended', reason = $reason, ended_at = $at,
                            waiting_since = NULL, waited_seconds = waited_seconds + $fold
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(now));
        cmd.Parameters.AddWithValue("$fold", fold);
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Run #{id} does not exist.");
        return ById(id)!;
    }

    public int CountSpawn(long id) => Increment(id, "spawns_used");

    public int CountExchange(long id) => Increment(id, "exchanges");

    private int Increment(long id, string column)
    {
        using var conn = db.Open();
        using (var update = conn.CreateCommand())
        {
            update.CommandText = $"UPDATE runs SET {column} = {column} + 1 WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Run #{id} does not exist.");
        }
        using var select = conn.CreateCommand();
        select.CommandText = $"SELECT {column} FROM runs WHERE id = $id";
        select.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(select.ExecuteScalar());
    }

    /// <summary>Upserts one phase's re-entry count, sets <c>runs.phase</c> to it, and returns the new
    /// count for THIS tag — never a total across tags.</summary>
    public int EnterPhase(long id, string phase, DateTimeOffset now)
    {
        using var conn = db.Open();
        using (var upsert = conn.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO run_phases (run_id, phase, entries) VALUES ($id, $phase, 1)
                ON CONFLICT (run_id, phase) DO UPDATE SET entries = entries + 1
                """;
            upsert.Parameters.AddWithValue("$id", id);
            upsert.Parameters.AddWithValue("$phase", phase);
            upsert.ExecuteNonQuery();
        }
        using (var setPhase = conn.CreateCommand())
        {
            setPhase.CommandText = "UPDATE runs SET phase = $phase WHERE id = $id";
            setPhase.Parameters.AddWithValue("$phase", phase);
            setPhase.Parameters.AddWithValue("$id", id);
            setPhase.ExecuteNonQuery();
        }
        using var select = conn.CreateCommand();
        select.CommandText = "SELECT entries FROM run_phases WHERE run_id = $id AND phase = $phase";
        select.Parameters.AddWithValue("$id", id);
        select.Parameters.AddWithValue("$phase", phase);
        return Convert.ToInt32(select.ExecuteScalar());
    }

    /// <summary>Every phase tag the run has entered, for the whole run: the policy needs the count
    /// for the tag being entered, which is not necessarily the tag being left.</summary>
    public IReadOnlyDictionary<string, int> PhaseEntries(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT phase, entries FROM run_phases WHERE run_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read()) map[reader.GetString(0)] = reader.GetInt32(1);
        return map;
    }

    /// <summary>Records who last touched a path: a second call for the same run and path keeps
    /// the LATEST author, never the first.</summary>
    public void RecordArtifact(long runId, string path, string authorId, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_artifacts (run_id, path, author_id, at) VALUES ($id, $path, $author, $at)
            ON CONFLICT (run_id, path) DO UPDATE SET author_id = excluded.author_id, at = excluded.at
            """;
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.Parameters.AddWithValue("$path", Normalize(path));
        cmd.Parameters.AddWithValue("$author", authorId);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(now));
        cmd.ExecuteNonQuery();
    }

    /// <summary><see cref="RecordArtifact"/> for one spawn's whole set of files, in one transaction,
    /// because a render can write thousands of frames.</summary>
    public void RecordArtifacts(long runId, IEnumerable<string> paths, string authorId, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO run_artifacts (run_id, path, author_id, at) VALUES ($id, $path, $author, $at)
            ON CONFLICT (run_id, path) DO UPDATE SET author_id = excluded.author_id, at = excluded.at
            """;
        cmd.Parameters.AddWithValue("$id", runId);
        var path = cmd.Parameters.Add("$path", SqliteType.Text);
        cmd.Parameters.AddWithValue("$author", authorId);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(now));
        foreach (var p in paths)
        {
            path.Value = Normalize(p);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<RunArtifact> Artifacts(long runId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, author_id, at FROM run_artifacts WHERE run_id = $id ORDER BY path";
        cmd.Parameters.AddWithValue("$id", runId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<RunArtifact>();
        while (reader.Read()) rows.Add(new RunArtifact(reader.GetString(0), reader.GetString(1), Timestamps.Parse(reader.GetString(2))));
        return rows;
    }

    /// <summary>Who last touched <paramref name="path"/>, matching however it was recorded — a
    /// leading <c>./</c>, backslashes, surrounding backticks/quotes, or different case — because it
    /// is normalised the same way on both sides. Null when nothing was ever recorded there.</summary>
    public string? ArtifactAuthor(long runId, string path)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT author_id FROM run_artifacts WHERE run_id = $id AND path = $path";
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.Parameters.AddWithValue("$path", Normalize(path));
        return cmd.ExecuteScalar() as string;
    }

    /// <summary><paramref name="runId"/> is nullable because "there is no run here" is itself a
    /// refusal the hub must record.</summary>
    public void RecordGateRun(long? runId, string roomId, string gate, string callerId, int? exitCode, string outcome, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_gate_runs (run_id, room_id, gate, caller_id, exit_code, outcome, at)
            VALUES ($run, $room, $gate, $caller, $exit, $outcome, $at)
            """;
        cmd.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$room", roomId);
        cmd.Parameters.AddWithValue("$gate", gate);
        cmd.Parameters.AddWithValue("$caller", callerId);
        cmd.Parameters.AddWithValue("$exit", (object?)exitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$at", Timestamps.Stamp(now));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<GateRun> GateRuns(long runId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT gate, caller_id, exit_code, outcome, at FROM run_gate_runs WHERE run_id = $id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", runId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<GateRun>();
        while (reader.Read())
            rows.Add(new GateRun(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3), Timestamps.Parse(reader.GetString(4))));
        return rows;
    }

    /// <summary>The one place the wall clock is computed: total time since start, minus every second
    /// the run spent parked, so an overnight restart-park does not re-park the instant it is resumed.
    /// Frozen while parked: <c>parked_seconds</c> has not yet absorbed the interval the run is
    /// currently sitting in, so while <see cref="Run.Status"/> is <see cref="RunStatus.Parked"/> with
    /// <see cref="Run.ParkedAt"/> set, elapsed is pinned at <c>(ParkedAt - StartedAt) -
    /// ParkedSeconds</c>. Waiting on a job is excluded the same way: <see cref="Run.WaitedSeconds"/> is
    /// subtracted, and an open wait pins elapsed at <see cref="Run.WaitingSince"/>.</summary>
    public static TimeSpan ActiveElapsed(Run r, DateTimeOffset now)
    {
        var end = r.Status == RunStatus.Parked && r.ParkedAt is { } parkedAt ? parkedAt
            : r.WaitingSince ?? now;
        return (end - r.StartedAt) - TimeSpan.FromSeconds(r.ParkedSeconds) - TimeSpan.FromSeconds(r.WaitedSeconds);
    }

    /// <summary>Strips one layer of surrounding backticks/quotes, backslashes to forward slashes,
    /// drops a leading <c>./</c>, trims, and lowercases. Used on write, on read, and by the critique
    /// rule that checks a path against the room's directory, so all three agree on one path.</summary>
    public static string Normalize(string path)
    {
        var p = path.Trim();
        if (p.Length >= 2)
        {
            var first = p[0];
            var last = p[^1];
            if ((first == '`' && last == '`') || (first == '"' && last == '"') || (first == '\'' && last == '\''))
                p = p[1..^1].Trim();
        }
        p = p.Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        if (ArtifactRef.IsReference(p) && p.AsSpan(ArtifactRef.Prefix.Length).StartsWith("./", StringComparison.Ordinal))
            p = ArtifactRef.Prefix + p[(ArtifactRef.Prefix.Length + 2)..];
        return p.Trim().ToLowerInvariant();
    }

    private static Run Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7) != 0, r.GetString(8), r.GetInt64(9),
        Timestamps.Parse(r.GetString(10)),
        r.IsDBNull(11) ? null : Timestamps.Parse(r.GetString(11)),
        r.GetInt64(12),
        r.IsDBNull(13) ? null : Timestamps.Parse(r.GetString(13)),
        r.GetInt32(14), r.GetInt32(15),
        r.GetInt32(16), r.GetInt32(17),
        r.IsDBNull(18) ? null : Timestamps.Parse(r.GetString(18)),
        r.GetInt64(19));
}
