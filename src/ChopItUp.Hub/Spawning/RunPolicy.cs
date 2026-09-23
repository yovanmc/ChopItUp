using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Spawning;

/// <summary>Everything the hub knows about a run when it decides what happens next. Assembled by the
/// service; the policy reads it and nothing else. Elapsed excludes parked time (RunStore.ActiveElapsed).
/// AnythingInFlight is scoped to this room (SpawnerService.InFlightIn), never the global
/// AnySpawnInFlight. PhaseEntries is the whole map, because the arm that matters needs the count for
/// the tag being entered, which is on the event, not the tag being left.
///
/// <see cref="RootMessageId"/> is the run's own <see cref="Run.RootMessageId"/> (the message that
/// started the run): every OpenConductor decision re-spawns the same run's conductor, and none of
/// the events that lead to one carries a root of its own.</summary>
public sealed record RunState(
    long RunId, string RoomId, string ConductorId, string Status, bool CapSpent,
    string Phase, IReadOnlyDictionary<string, int> PhaseEntries,
    int SpawnsUsed, int Exchanges, TimeSpan Elapsed,
    int RefusalsThisPhase, int SilencesThisPhase,
    bool ExchangeOpen, bool AnythingInFlight,
    long RootMessageId);

/// <summary>The one thing that just happened to a run. The service raises exactly one of these per
/// call to <see cref="RunPolicy.Decide"/>.</summary>
public abstract record RunEvent
{
    /// <summary>The room's current exchange reached Concluded. The service does not raise this for an
    /// exchange the conductor has already superseded by rooting a newer one.</summary>
    public sealed record ExchangeConcluded(long LastMessageId) : RunEvent;

    /// <summary><see cref="Refusal"/> is null exactly when the post passed every class rule and
    /// carries a valid <see cref="Tag"/>; otherwise it names which rule failed and <see cref="Tag"/>
    /// may or may not be present.</summary>
    public sealed record ConductorPosted(long MessageId, PhaseTag? Tag, IReadOnlyList<string> Mentioned, string? Refusal) : RunEvent;

    public sealed record SpawnSilent(string ParticipantId, IReadOnlyList<long> TriggerIds) : RunEvent;

    public sealed record HumanPosted(long MessageId) : RunEvent;

    public sealed record StopRequested : RunEvent;

    public sealed record Tick : RunEvent;
}

/// <summary>What the hub should do about a run, and nothing else - no database write, no post, no
/// process launch. The service carries every decision out.</summary>
public abstract record RunDecision
{
    public sealed record Nothing : RunDecision;

    public sealed record OpenConductor(long RootMessageId, IReadOnlyList<long> TriggerIds) : RunDecision;

    public sealed record OpenWorkers(long RootMessageId, IReadOnlyList<string> Mentioned, PhaseTag Tag) : RunDecision;

    /// <summary>Post the note and stop. Split from RefuseAndAsk because a refused conductor post wants
    /// a re-ask and a refused resume does not, and one decision carrying two obligations would push
    /// the choice back into the service.</summary>
    public sealed record Refuse(string Note) : RunDecision;

    public sealed record RefuseAndAsk(string Note, long TriggerMessageId) : RunDecision;

    public sealed record Park(string Reason, bool CapSpent) : RunDecision;

    /// <summary><paramref name="Cause"/> says who ended the run, decided here rather than inferred by
    /// <c>SpawnerService</c> from <paramref name="Reason"/>'s text.</summary>
    public sealed record End(string Reason, ExchangeStopCause Cause) : RunDecision;
}

/// <summary>The run loop as one pure state machine. Takes what the hub knows about a run
/// (<see cref="RunState"/>) plus one thing that just happened (<see cref="RunEvent"/>) and returns one
/// decision (<see cref="RunDecision"/>). No database, no process, no clock of its own: every one of
/// those lives in <c>SpawnerService</c>, which raises the events, assembles the state and carries the
/// decision out.
///
/// The hard caps (spawns, wall clock) are checked first for every event except
/// <see cref="RunEvent.StopRequested"/>: an owner stopping a cap-exhausted run must get
/// <see cref="RunDecision.End"/>, not <see cref="RunDecision.Park"/>, or the stop could not end the
/// run without resuming it first.
///
/// The cap checks also require <see cref="RunState.Status"/> to be <see cref="RunStatus.Active"/>.
/// A parked or ended run's <c>SpawnsUsed</c>/<c>Elapsed</c> stay at or over a spent cap forever, so
/// without this guard a <see cref="RunEvent.HumanPosted"/> resume against any parked run would
/// re-Park here: a hard-capped park would re-Park instead of the one-line
/// <see cref="RunDecision.Refuse"/>, and a soft park would re-Park as a spent wall-clock cap purely
/// from having sat parked. A run that is not running cannot trip a cap again.</summary>
public sealed class RunPolicy(RunLimits limits)
{
    /// <summary>The cap-spent refusal text, public so the service posts the identical line rather
    /// than duplicating the string.</summary>
    public const string CapSpentRefusal = "this run is parked because a cap is spent; /stop and start a new one";

    public RunDecision Decide(RunState s, RunEvent e, IReadOnlyList<long> pendingSteers)
    {
        if (e is not RunEvent.StopRequested && s.Status == RunStatus.Active)
        {
            if (s.SpawnsUsed >= limits.Spawns)
                return new RunDecision.Park($"the run used its {limits.Spawns} spawns", true);
            if (s.Elapsed >= limits.WallClock)
                return new RunDecision.Park($"the run passed {limits.WallClock} of active time", true);
        }

        return e switch
        {
            RunEvent.ConductorPosted p => DecideConductorPosted(s, p),
            RunEvent.ExchangeConcluded x => DecideExchangeConcluded(s, x, pendingSteers),
            RunEvent.SpawnSilent sp => DecideSpawnSilent(s, sp),
            RunEvent.HumanPosted h => DecideHumanPosted(s, h),
            RunEvent.StopRequested => new RunDecision.End("stopped by the owner", ExchangeStopCause.Owner),
            RunEvent.Tick => DecideTick(s, pendingSteers),
            _ => throw new ArgumentOutOfRangeException(nameof(e), e, "unknown RunEvent"),
        };
    }

    private RunDecision DecideConductorPosted(RunState s, RunEvent.ConductorPosted p)
    {
        if (p.Refusal is not null)
        {
            if (s.RefusalsThisPhase == 0) return new RunDecision.RefuseAndAsk(p.Refusal, p.MessageId);
            return new RunDecision.Park($"two refused posts in phase {s.Phase}", false);
        }

        var tag = p.Tag ?? throw new ArgumentException(
            "a ConductorPosted with no Refusal must carry a phase Tag", nameof(p));

        if (tag.Kind == "ping") return new RunDecision.End("the conductor pinged", ExchangeStopCause.Run);

        var key = tag.ToString();
        var entries = s.PhaseEntries.GetValueOrDefault(key);
        if (entries >= limits.PhaseEntries)
            return new RunDecision.Park($"phase {key} entered {entries} times", true);

        return new RunDecision.OpenWorkers(p.MessageId, p.Mentioned, tag);
    }

    private static RunDecision DecideExchangeConcluded(RunState s, RunEvent.ExchangeConcluded x, IReadOnlyList<long> pendingSteers)
    {
        // The concluded exchange was already superseded, or something else is still driving.
        if (s.ExchangeOpen || s.AnythingInFlight) return new RunDecision.Nothing();

        var triggers = new List<long> { x.LastMessageId };
        triggers.AddRange(pendingSteers);
        return new RunDecision.OpenConductor(s.RootMessageId, triggers);
    }

    private RunDecision DecideSpawnSilent(RunState s, RunEvent.SpawnSilent sp)
    {
        if (sp.ParticipantId != s.ConductorId) return new RunDecision.Nothing();

        if (s.SilencesThisPhase == 0) return new RunDecision.OpenConductor(s.RootMessageId, sp.TriggerIds);

        var entries = s.PhaseEntries.GetValueOrDefault(s.Phase);
        if (entries >= limits.PhaseEntries) return new RunDecision.Park("the conductor did not post", false);

        // The service counts a phase entry for this ask.
        return new RunDecision.OpenConductor(s.RootMessageId, sp.TriggerIds);
    }

    private static RunDecision DecideHumanPosted(RunState s, RunEvent.HumanPosted h)
    {
        // The service records the steer.
        if (s.Status == RunStatus.Active) return new RunDecision.Nothing();

        if (s.Status == RunStatus.Parked)
        {
            if (s.CapSpent)
                return new RunDecision.Refuse(CapSpentRefusal);
            return new RunDecision.OpenConductor(s.RootMessageId, [h.MessageId]);
        }

        // Ended, or any other status: nothing left to steer.
        return new RunDecision.Nothing();
    }

    private static RunDecision DecideTick(RunState s, IReadOnlyList<long> pendingSteers)
    {
        if (s.Status != RunStatus.Active || s.ExchangeOpen || s.AnythingInFlight) return new RunDecision.Nothing();

        return pendingSteers.Count > 0
            ? new RunDecision.OpenConductor(s.RootMessageId, pendingSteers)
            : new RunDecision.Park("the run stalled", false);
    }
}
