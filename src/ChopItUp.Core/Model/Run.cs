namespace ChopItUp.Core.Model;

/// <summary>The status values a run row can hold (schema v8).</summary>
public static class RunStatus
{
    public const string Active = "active";
    public const string Parked = "parked";
    public const string Ended = "ended";
}

/// <summary>One row of the <c>runs</c> table: a conductor-driven, multi-phase piece of work bound to
/// one directory room, persisted so it survives a restart. <see cref="CapSpent"/> is true only when
/// the run is parked because a hard cap (spawns, wall clock, phase re-entry) is spent: the property
/// that decides whether a park can be resumed. <see cref="Phase"/> is the phase tag last entered,
/// defaulting to <c>(start)</c> before the conductor has announced one. <see cref="BudgetHours"/> and
/// <see cref="SpawnBudget"/> are the run's caps (schema v17), set at start and raised by
/// <c>/continue</c>. <see cref="WaitingSince"/> is set while the run waits on a job with nothing else
/// driving it, and <see cref="WaitedSeconds"/> holds every finished wait: neither counts as active
/// time. <see cref="ResumeAt"/> (schema v19) is set only on a run parked for a usage limit: the
/// instant it reopens its conductor by itself.</summary>
public sealed record Run(
    long Id, string RoomId, string ConductorId, string SkillName, string Arguments,
    string Status, string? Reason, bool CapSpent, string Phase, long RootMessageId,
    DateTimeOffset StartedAt, DateTimeOffset? ParkedAt, long ParkedSeconds, DateTimeOffset? EndedAt,
    int SpawnsUsed, int Exchanges,
    int BudgetHours, int SpawnBudget, DateTimeOffset? WaitingSince, long WaitedSeconds, DateTimeOffset? ResumeAt = null);

/// <summary>Who wrote which file path, read out of a spawn's own git diff rather than a
/// self-report.</summary>
public sealed record RunArtifact(string Path, string AuthorId, DateTimeOffset At);

/// <summary>One recorded invocation (or refusal) of <c>run_gate</c>.</summary>
public sealed record GateRun(string Gate, string CallerId, int? ExitCode, string Outcome, DateTimeOffset At);
