using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChopItUp.Core.Messaging;
using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Rooms;
using ChopItUp.Hub.Security;
using ChopItUp.Hub.Spawning;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ChopItUp.Hub.Mcp;

/// <summary>Background jobs for a run's spawns: a long or GPU command that outlives the spawn that
/// starts it, and wakes the run's conductor when it ends. Only a spawn in flight in the room's
/// active run may start one, so an app-backed host (a host-file participant) never can. Every
/// refusal posts a fixed-form hub note before it throws, so the trail shows it even when the caller
/// drops the error.</summary>
[McpServerToolType]
public sealed class JobTools(JobRunner jobs, JobStore store, RunStore runs, SpawnerService spawner, MessageStore messages,
    MessageSignal signal, RoomArtifacts artifacts, IReadOnlyList<Participant> roster, IHttpContextAccessor http)
{
    private const int CommandNoteChars = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Caller =>
        http.HttpContext?.Items[BearerTokenMiddleware.ParticipantKey] as string
        ?? throw new McpException("Unauthenticated request reached a tool; this is a hub bug.");

    [McpServerTool(Name = "start_job", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true),
     Description("Start a long-running command as a background job that keeps running after your turn ends. The command is a PowerShell script run with the hub's full rights (git writes and network included), in its own folder under the room's artifacts folder; write its outputs there. It logs to job.log in that folder. When it ends, the hub posts its exit code and last log lines and wakes the run's conductor. gpu: true waits for the machine-wide GPU slot and holds it for the job's whole life; give a cancel_command that interrupts the GPU service's render, which the hub runs before stopping the job. timeout_minutes: 1 to 480, default 120. Only a spawn in flight in this room's active run may call this.")]
    public string StartJob(
        [Description("Room id, e.g. \"lab\".")] string room_id,
        [Description("PowerShell script text to run.")] string command,
        [Description("True when the job uses the GPU.")] bool gpu = false,
        [Description("Minutes before the hub stops the job, 1 to 480. Default 120.")] int? timeout_minutes = null,
        [Description("PowerShell script text the hub runs before it stops the job early, e.g. a GPU service's interrupt.")] string? cancel_command = null)
    {
        var me = Caller;
        var room = Gate("start_job", room_id, me, needsRun: true);
        var timeout = timeout_minutes ?? JobRunner.DefaultTimeoutMinutes;
        if (timeout is < 1 or > JobRunner.MaxTimeoutMinutes)
            throw Refuse("start_job", room_id, me, "bad-timeout", $"timeout_minutes must be 1 to {JobRunner.MaxTimeoutMinutes}.");
        if (string.IsNullOrWhiteSpace(command))
            throw Refuse("start_job", room_id, me, "no-command", "command is empty.");
        var lease = artifacts.Ensure(room.Directory!);
        if (lease.Path is null)
            throw Refuse("start_job", room_id, me, "no-artifacts-folder", $"The room's artifacts folder is not usable: {lease.Refusal}");

        Job job;
        try { job = jobs.Start(room_id, runs.Active(room_id)!.Id, me, command, string.IsNullOrWhiteSpace(cancel_command) ? null : cancel_command, gpu, timeout, lease.Path); }
        catch (IOException e) { throw Refuse("start_job", room_id, me, "no-job-folder", $"The job's folder could not be made: {e.Message}"); }

        var shown = command.Length <= CommandNoteChars ? command : command[..CommandNoteChars] + "…";
        PostNote(room_id, $"Job {job.Name} started by @{me} (gpu: {(gpu ? "yes" : "no")}, limit {timeout} min). Command:\n{Fenced(shown)}");
        return JsonSerializer.Serialize(new
        {
            id = job.Name,
            folder = job.Folder,
            artifact_prefix = ArtifactPrefix(job),
            log = Path.Combine(job.Folder, JobRunner.LogFile),
            timeout_minutes = timeout,
        }, JsonOptions);
    }

    [McpServerTool(Name = "job_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Status of a background job in this room: running or how it ended, its exit code, its command and the last lines of its log. Only a spawn in flight in this room may call this.")]
    public string GetJobStatus(
        [Description("Room id, e.g. \"lab\".")] string room_id,
        [Description("Job id from start_job, e.g. \"j12\".")] string id)
    {
        var me = Caller;
        Gate("job_status", room_id, me, needsRun: false);
        var job = JobIn("job_status", room_id, me, id);
        return JsonSerializer.Serialize(new
        {
            id = job.Name,
            status = job.Status,
            exit_code = job.ExitCode,
            started_by = job.AuthorId,
            gpu = job.Gpu,
            command = job.Command,
            started_at = job.StartedAt,
            ended_at = job.EndedAt,
            artifact_prefix = ArtifactPrefix(job),
            log_tail = JobRunner.Tail(Path.Combine(job.Folder, JobRunner.LogFile)),
        }, JsonOptions);
    }

    [McpServerTool(Name = "cancel_job", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Stop a running background job in this room: the hub runs its cancel_command, then kills it, and posts how it ended. Only a spawn in flight in this room may call this.")]
    public async Task<string> CancelJob(
        [Description("Room id, e.g. \"lab\".")] string room_id,
        [Description("Job id from start_job, e.g. \"j12\".")] string id)
    {
        var me = Caller;
        Gate("cancel_job", room_id, me, needsRun: false);
        var job = JobIn("cancel_job", room_id, me, id);
        var stopped = await jobs.CancelAsync(job.Id);
        var now = store.Get(job.Id)!;
        return JsonSerializer.Serialize(new { id = now.Name, status = now.Status, was_running = stopped }, JsonOptions);
    }

    /// <summary>The checks every job tool shares, in a fixed order.</summary>
    private Room Gate(string tool, string roomId, string caller, bool needsRun)
    {
        if (roster.FirstOrDefault(p => p.Id == caller) is not { } participant || !ExchangePolicy.IsSpawnable(participant))
            throw Refuse(tool, roomId, caller, "not-a-spawn", $"{tool} may only be called by a spawn the hub started, not by an app-backed host.");
        if (messages.GetRoom(roomId) is not { } room)
            throw new McpException($"Unknown room '{roomId}'. Call list_rooms.");
        if (room.Directory is null)
            throw Refuse(tool, roomId, caller, "no-directory", $"Room '{roomId}' has no directory.");
        if (needsRun && runs.Active(roomId) is null)
            throw Refuse(tool, roomId, caller, "no-active-run", $"No run is active in room '{roomId}'.");
        if (!spawner.Snapshot(roomId).InFlight.Contains(caller))
            throw Refuse(tool, roomId, caller, "not-in-flight", $"{tool} may only be called by a spawn in flight in room '{roomId}'.");
        return room;
    }

    private Job JobIn(string tool, string roomId, string caller, string id) =>
        Job.IdFrom(id) is { } rowId && store.Get(rowId) is { } job && job.RoomId == roomId
            ? job
            : throw Refuse(tool, roomId, caller, "unknown-job", $"No job '{id}' in room '{roomId}'.");

    private static string ArtifactPrefix(Job job) => $"{ArtifactRef.Prefix}{RoomArtifacts.JobsFolder}/{Path.GetFileName(job.Folder)}/";

    private McpException Refuse(string tool, string roomId, string caller, string slug, string message)
    {
        if (messages.RoomExists(roomId)) PostNote(roomId, $"{tool} by @{caller}: refused ({slug})");
        return new McpException(message);
    }

    private static string Fenced(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}\n{text}\n{fence}";
    }

    private void PostNote(string roomId, string text)
    {
        try { signal.Publish(roomId, messages.Post(roomId, ChopDb.HubParticipantId, text)); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"jobs: note to '{roomId}' not posted ({e.GetType().Name}: {e.Message}): {text.Split('\n')[0]}");
        }
    }
}
