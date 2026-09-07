using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Web;

/// <summary>What the room's run strip reads (row 19, task 14). One GET, shaped like
/// <see cref="ExchangeApi"/>: no auth, loopback is the boundary, and a room the hub does not know is
/// a 404 rather than an empty answer. A room that has simply never had a run is 204 — "nothing here",
/// which the strip renders as nothing at all.</summary>
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
/// <see cref="ElapsedMinutes"/> comes from <see cref="RunStore.ActiveElapsed"/> and nowhere else: the
/// wall-clock cap counts time the run spent ACTIVE, so parked time is excluded and a parked run's
/// clock is frozen where it stood. An ended run is read at its own <c>endedAt</c> for the same
/// reason — its clock stopped when it did, and a finished run whose elapsed kept climbing would be
/// the same lie one minute later.</summary>
public sealed record RunSnapshot(
    long Id, string RoomId, string ConductorId, string SkillName, string Status, string? Reason,
    bool CapSpent, string Phase, int PhaseEntries, int PhaseEntryCap, int Exchanges, int SpawnsUsed,
    int SpawnCap, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int ElapsedMinutes,
    int WallClockCapMinutes, IReadOnlyList<RunArtifact> Artifacts, IReadOnlyList<GateRun> GateRuns)
{
    public static RunSnapshot Of(Run run, RunStore runs, RunLimits limits, DateTimeOffset now) => new(
        run.Id, run.RoomId, run.ConductorId, run.SkillName, run.Status, run.Reason, run.CapSpent,
        run.Phase, runs.PhaseEntries(run.Id).GetValueOrDefault(run.Phase), limits.PhaseEntries,
        run.Exchanges, run.SpawnsUsed, limits.Spawns,
        run.StartedAt, run.EndedAt,
        (int)RunStore.ActiveElapsed(run, run.EndedAt ?? now).TotalMinutes,
        (int)limits.WallClock.TotalMinutes,
        runs.Artifacts(run.Id), runs.GateRuns(run.Id));
}
