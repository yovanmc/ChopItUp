using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>A room's spawn tasks, newest launch first. One GET, open like <see cref="RunsApi"/>
/// (the owner-bearer gate guards only non-GET <c>/api</c> requests); an unknown room is 404. The
/// rows and the in-flight spawn ids are read together on the spawner loop, so an open row whose
/// spawn is not in flight is honestly interrupted. While the hub stops the loop is gone: 503.</summary>
public static class TasksApi
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static void MapTasksApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/rooms/{roomId}/tasks", GetTasks);
    }

    private static async Task<IResult> GetTasks(string roomId, int? limit, MessageStore store, TaskStore tasks, SpawnerService spawner)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        (IReadOnlyList<TaskRecord> Rows, IReadOnlySet<string> InFlight) read;
        try { read = await spawner.TasksAsync(roomId, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit)); }
        catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        return Results.Json(read.Rows.Select(t => TaskView.Of(t, read.InFlight.Contains(t.Start.SpawnId), tasks)).ToList());
    }
}

/// <summary>One task as the API answers it. <see cref="Termination"/> is null while the spawn runs and
/// <c>interrupted</c> for an open row whose spawn is gone; that row's <see cref="Posts"/> is the count
/// of messages it stamped, and its other finish fields stay null. Token numbers come from
/// <see cref="TokenMetrics"/>.</summary>
public sealed record TaskView(
    string SpawnId, string ParticipantId, string Host, string? RequestedModel, string? Effort, long RootMessageId, int Turn,
    long? RunId, bool Retry, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, long? DurationMs, int? ExitCode, string? Termination,
    int? ToolCalls, int? Posts, long? InputTokens, long? OutputTokens, long? CacheReadTokens, long? CacheWriteTokens, IReadOnlyList<string> ReportedModels)
{
    public static TaskView Of(TaskRecord t, bool inFlight, TaskStore tasks)
    {
        var s = t.Start;
        var e = t.End;
        var interrupted = e is null && !inFlight;
        var m = TokenMetrics.From(e?.Usage);
        return new(s.SpawnId, s.ParticipantId, s.Host, s.RequestedModel, s.Effort, s.RootMessageId, s.Turn, s.RunId, s.Retry, s.StartedAt,
            e?.EndedAt, e?.DurationMs, e?.ExitCode, e?.Termination ?? (interrupted ? TaskTermination.Interrupted : null),
            e?.ToolCalls, e?.Posts ?? (interrupted ? tasks.StampedPosts(s.SpawnId) : null),
            m.InputTokens, m.OutputTokens, m.CacheReadTokens, m.CacheWriteTokens, m.ReportedModels);
    }
}
