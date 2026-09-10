using ChopItUp.Core.Model;

namespace ChopItUp.Hub.Spawning;

/// <summary>Everything the hub knows about a run when it decides what happens next. Assembled by the
/// service; the policy reads it and nothing else. Elapsed EXCLUDES parked time (RunStore.ActiveElapsed).
/// AnythingInFlight is scoped to THIS ROOM (SpawnerService.InFlightIn), never the global
/// AnySpawnInFlight. PhaseEntries is the whole map, because the arm that matters needs the count for
/// the tag being ENTERED, which is on the event, not the tag being left.
///
/// <see cref="RootMessageId"/> is a builder addition beyond the plan's pasted record shape (row19-runs
/// task 3): table rows 8, 10, 12, 15 and 18 all return <see cref="RunDecision.OpenConductor"/>, and
/// that decision's RootMessageId has to come from somewhere - neither the pasted RunState nor any of
/// those events (ExchangeConcluded, SpawnSilent, HumanPosted, Tick) carries one. Every OpenConductor in
/// the table is re-spawning the SAME run's conductor, so the one stable value available is the run's
/// own <see cref="Run.RootMessageId"/> (schema v8, task 1) - the message that started the run. Reported
/// as a deviation in the task 3 build report.</summary>
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
    /// <summary>The room's CURRENT exchange reached Concluded. The service does not raise this for an
    /// exchange the conductor has already superseded by rooting a newer one (pass 2 F-1).</summary>
    public sealed record ExchangeConcluded(long LastMessageId) : RunEvent;

    /// <summary><see cref="Refusal"/> is null exactly when the post passed every D8 class rule and
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

    /// <summary>Post the note and stop. Split from RefuseAndAsk because AC6 wants a re-ask and AC15
    /// does not, and one decision carrying two obligations pushes the choice back into the service,
    /// which P7 forbids (pass 2 F-11).</summary>
    public sealed record Refuse(string Note) : RunDecision;

    public sealed record RefuseAndAsk(string Note, long TriggerMessageId) : RunDecision;

    public sealed record Park(string Reason, bool CapSpent) : RunDecision;

    /// <summary>Row 27: <paramref name="Cause"/> says who ended the run, decided here (P7) rather
    /// than inferred by <c>SpawnerService</c> from <paramref name="Reason"/>'s text.</summary>
    public sealed record End(string Reason, ExchangeStopCause Cause) : RunDecision;
}

/// <summary>The run loop as one pure state machine (P7). Takes what the hub knows about a run
/// (<see cref="RunState"/>) plus one thing that just happened (<see cref="RunEvent"/>) and returns one
/// decision (<see cref="RunDecision"/>). No database, no process, no clock of its own - every one of
/// those lives in <c>SpawnerService</c>, which raises the events, assembles the state and carries the
/// decision out.
///
/// Rows 1 and 2 of the transition table are checked first for EVERY event EXCEPT
/// <see cref="RunEvent.StopRequested"/>: an owner stopping a cap-exhausted run must get
/// <see cref="RunDecision.End"/>, not <see cref="RunDecision.Park"/>, or AC11 ("without resuming the
/// run first") cannot be satisfied on the state that most needs it.
///
/// **Rows 1/2 also require <see cref="RunState.Status"/> to be <see cref="RunStatus.Active"/>**
/// (orchestrator diff-review finding against task 9f). A parked or ended run's <c>SpawnsUsed</c>/
/// <c>Elapsed</c> stay at or over a spent cap forever, so without this guard a <see
/// cref="RunEvent.HumanPosted"/> resume attempt against ANY parked run - soft or hard - would re-Park
/// here before row 15/16 ever run: a hard-capped park re-Parks instead of the one-line row 16
/// <see cref="RunDecision.Refuse"/> AC15 requires, and a soft park re-Parks as a spent wall-clock cap
/// purely from having sat parked (the very case row 15 exists to resume). A run that is not running
/// cannot trip a cap again.</summary>
public sealed class RunPolicy(RunLimits limits)
{
    /// <summary>Row 16's exact text - a public constant (row 19, task 9f) so the service can post the
    /// identical refusal when it short-circuits AROUND <see cref="Decide"/> for a resume-of-a-parked-
    /// run (rows 1/2's pre-check would misfire on a parked run's stale, pre-resume Elapsed/SpawnsUsed
    /// either way - pass 2's F-4), rather than duplicating the string.</summary>
    public const string CapSpentRefusal = "this run is parked because a cap is spent; /stop and start a new one";

    public RunDecision Decide(RunState s, RunEvent e, IReadOnlyList<long> pendingSteers)
    {
        if (e is not RunEvent.StopRequested && s.Status == RunStatus.Active)
        {
            // Row 1.
            if (s.SpawnsUsed >= limits.Spawns)
                return new RunDecision.Park($"the run used its {limits.Spawns} spawns", true);
            // Row 2.
            if (s.Elapsed >= limits.WallClock)
                return new RunDecision.Park($"the run passed {limits.WallClock} of active time", true);
        }

        return e switch
        {
            RunEvent.ConductorPosted p => DecideConductorPosted(s, p),
            RunEvent.ExchangeConcluded x => DecideExchangeConcluded(s, x, pendingSteers),
            RunEvent.SpawnSilent sp => DecideSpawnSilent(s, sp),
            RunEvent.HumanPosted h => DecideHumanPosted(s, h),
            RunEvent.StopRequested => new RunDecision.End("stopped by the owner", ExchangeStopCause.Owner), // Row 17.
            RunEvent.Tick => DecideTick(s, pendingSteers),
            _ => throw new ArgumentOutOfRangeException(nameof(e), e, "unknown RunEvent"),
        };
    }

    private RunDecision DecideConductorPosted(RunState s, RunEvent.ConductorPosted p)
    {
        if (p.Refusal is not null)
        {
            // Row 3.
            if (s.RefusalsThisPhase == 0) return new RunDecision.RefuseAndAsk(p.Refusal, p.MessageId);
            // Row 4.
            return new RunDecision.Park($"two refused posts in phase {s.Phase}", false);
        }

        var tag = p.Tag ?? throw new ArgumentException(
            "a ConductorPosted with no Refusal must carry a phase Tag", nameof(p));

        // Row 5.
        if (tag.Kind == "ping") return new RunDecision.End("the conductor pinged", ExchangeStopCause.Run);

        var key = tag.ToString();
        var entries = s.PhaseEntries.GetValueOrDefault(key);
        // Row 6.
        if (entries >= limits.PhaseEntries)
            return new RunDecision.Park($"phase {key} entered {entries} times", true);

        // Row 7.
        return new RunDecision.OpenWorkers(p.MessageId, p.Mentioned, tag);
    }

    private static RunDecision DecideExchangeConcluded(RunState s, RunEvent.ExchangeConcluded x, IReadOnlyList<long> pendingSteers)
    {
        // Row 9: the concluded exchange was already superseded, or something else is still driving.
        if (s.ExchangeOpen || s.AnythingInFlight) return new RunDecision.Nothing();

        // Row 8.
        var triggers = new List<long> { x.LastMessageId };
        triggers.AddRange(pendingSteers);
        return new RunDecision.OpenConductor(s.RootMessageId, triggers);
    }

    private RunDecision DecideSpawnSilent(RunState s, RunEvent.SpawnSilent sp)
    {
        // Row 13.
        if (sp.ParticipantId != s.ConductorId) return new RunDecision.Nothing();

        // Row 10.
        if (s.SilencesThisPhase == 0) return new RunDecision.OpenConductor(s.RootMessageId, sp.TriggerIds);

        var entries = s.PhaseEntries.GetValueOrDefault(s.Phase);
        // Row 11.
        if (entries >= limits.PhaseEntries) return new RunDecision.Park("the conductor did not post", false);

        // Row 12: the service counts a phase entry for this ask.
        return new RunDecision.OpenConductor(s.RootMessageId, sp.TriggerIds);
    }

    private static RunDecision DecideHumanPosted(RunState s, RunEvent.HumanPosted h)
    {
        // Row 14: the service records the steer.
        if (s.Status == RunStatus.Active) return new RunDecision.Nothing();

        if (s.Status == RunStatus.Parked)
        {
            // Row 16.
            if (s.CapSpent)
                return new RunDecision.Refuse(CapSpentRefusal);
            // Row 15.
            return new RunDecision.OpenConductor(s.RootMessageId, [h.MessageId]);
        }

        // Ended, or any other status: nothing left to steer.
        return new RunDecision.Nothing();
    }

    private static RunDecision DecideTick(RunState s, IReadOnlyList<long> pendingSteers)
    {
        // Row 20.
        if (s.Status != RunStatus.Active || s.ExchangeOpen || s.AnythingInFlight) return new RunDecision.Nothing();

        // Row 18 / Row 19.
        return pendingSteers.Count > 0
            ? new RunDecision.OpenConductor(s.RootMessageId, pendingSteers)
            : new RunDecision.Park("the run stalled", false);
    }
}
