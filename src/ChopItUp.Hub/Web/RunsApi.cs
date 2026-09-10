using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>What the room's run strip reads (row 19, task 14). One GET, shaped like
/// <see cref="ExchangeApi"/>: unauthenticated, since row 28's owner-bearer gate
/// (<c>BearerTokenMiddleware</c>) only guards non-GET <c>/api</c> requests, and a room the hub does
/// not know is a 404 rather than an empty answer. A room that has simply never had a run is 204 —
/// "nothing here", which the strip renders as nothing at all.</summary>
public static class RunsApi
{
    public static void MapRunsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/rooms/{roomId}/run", GetRun);
    }

    private static IResult GetRun(string roomId, MessageStore store, RunStore runs, RunLimits limits, TimeProvider clock)
    {
        if (!store.RoomExists(roomId)) return Results.NotFound(new { error = $"Unknown room '{roomId}'." });
        var run = runs.Active(roomId) ?? runs.Latest(roomId);
        return run is null ? Results.NoContent() : Results.Json(RunSnapshot.Of(run, runs, limits, clock.GetUtcNow()));
    }
}

/// <summary>The run as the UI sees it. Counters and caps travel together — a count with no cap beside
/// it is a number the owner has to remember the meaning of.
///
/// <see cref="PhaseEntries"/> is entries into the phase the run is in RIGHT NOW, never a total across
/// tags, because the cap it is read against is per tag (<see cref="RunLimits.PhaseEntries"/>).
///
/// <see cref="PhaseHistory"/> is every tag the run has entered, with its own count. The strip does not
/// draw it; the M19 live check reads it, because "the run entered two distinct phases" is otherwise
/// only answerable by polling this endpoint and unioning whatever the polls happen to catch — and a
/// phase the run left between two polls would then FAIL exactly like a conductor that never entered
/// it. Task 15d exists to remove that ambiguity, not to add another source of it.
///
/// <see cref="ElapsedMinutes"/> comes from <see cref="RunStore.ActiveElapsed"/> and nowhere else: the
/// wall-clock cap counts time the run spent ACTIVE, so parked time is excluded and a parked run's
/// clock is frozen where it stood. An ended run is read at its own <c>endedAt</c> for the same
/// reason — its clock stopped when it did, and a finished run whose elapsed kept climbing would be
/// the same lie one minute later.</summary>
public sealed record RunSnapshot(
    long Id, string RoomId, string ConductorId, string SkillName, string Status, string? Reason,
    bool CapSpent, string Phase, int PhaseEntries, int PhaseEntryCap, int Exchanges, int SpawnsUsed,
    int SpawnCap, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int ElapsedMinutes,
    int WallClockCapMinutes, IReadOnlyList<RunArtifact> Artifacts, IReadOnlyList<GateRun> GateRuns,
    IReadOnlyDictionary<string, int> PhaseHistory)
{
    public static RunSnapshot Of(Run run, RunStore runs, RunLimits limits, DateTimeOffset now)
    {
        // One read, two uses: the current tag's count and the whole map are the same query.
        var entries = runs.PhaseEntries(run.Id);
        return new(
        run.Id, run.RoomId, run.ConductorId, run.SkillName, run.Status, run.Reason, run.CapSpent,
        run.Phase, entries.GetValueOrDefault(run.Phase), limits.PhaseEntries,
        run.Exchanges, run.SpawnsUsed, limits.Spawns,
        run.StartedAt, run.EndedAt,
        (int)RunStore.ActiveElapsed(run, run.EndedAt ?? now).TotalMinutes,
        (int)limits.WallClock.TotalMinutes,
        runs.Artifacts(run.Id), runs.GateRuns(run.Id), entries);
    }
}
