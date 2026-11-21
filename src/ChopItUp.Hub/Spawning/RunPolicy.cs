using ChopItUp.Core.Model;
using ChopItUp.Core.Skills;

namespace ChopItUp.Hub.Spawning;

/// <summary>Everything the hub knows about a run when it decides what happens next. Assembled by the
/// service; the policy reads it and nothing else. Elapsed excludes parked time (RunStore.ActiveElapsed).
/// AnythingInFlight is scoped to this room (SpawnerService.InFlightIn), never the global
/// AnySpawnInFlight. PhaseEntries is the whole map, because the arm that matters needs the count for
/// the tag being entered, which is on the event, not the tag being left.
///
/// <see cref="RootMessageId"/> is the run's own <see cref="Run.RootMessageId"/> (the message that
/// started the run): every OpenConductor decision re-spawns the same run's conductor, and none of
/// the events that lead to one carries a root of its own.
///
/// <see cref="SpawnBudget"/> and <see cref="WallClockBudget"/> are this run's caps, read from its
/// row. Elapsed also excludes time spent waiting on a job. <see cref="JobsInFlight"/> counts the
/// background jobs this run has running. <see cref="ConductorInFlight"/> is true while the run's
/// conductor spawn is running in its room.</summary>
public sealed record RunState(
    long RunId, string RoomId, string ConductorId, string Status, bool CapSpent,
    string Phase, IReadOnlyDictionary<string, int> PhaseEntries,
    int SpawnsUsed, int Exchanges, TimeSpan Elapsed,
    int RefusalsThisPhase, int SilencesThisPhase,
    bool ExchangeOpen, bool AnythingInFlight,
    long RootMessageId,
    int SpawnBudget, TimeSpan WallClockBudget, int JobsInFlight, bool ConductorInFlight = false)
{
    /// <summary>A cap is reached by the numbers, whatever the cap-spent flag says: a restart park
    /// can hold a run whose active time already passed its budget.</summary>
    public bool OverBudget => SpawnsUsed >= SpawnBudget || Elapsed >= WallClockBudget;
}

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

    /// <summary>A human `/continue` in a room whose run is active or parked, with the `hours:` it
    /// carried.</summary>
    public sealed record Continue(long MessageId, HoursToken Hours) : RunEvent;

    /// <summary>One of the run's background jobs ended. The hub already posted its note.</summary>
    public sealed record JobFinished(long NoteMessageId) : RunEvent;
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

    /// <summary>A parked run gets <paramref name="Hours"/> more budget hours: its budget becomes
    /// <paramref name="BudgetHours"/> and <paramref name="SpawnBudget"/>, counted from what it already
    /// used when that passed the old budget. Its phase-entry counts reset, it resumes and its conductor
    /// opens.</summary>
    public sealed record Extend(int Hours, int BudgetHours, int SpawnBudget, long RootMessageId, IReadOnlyList<long> TriggerIds) : RunDecision;

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
/// The hard caps (the run's spawn and wall-clock budget) are checked first for every event except
/// three. <see cref="RunEvent.StopRequested"/>: an owner stopping a cap-exhausted run must get
/// <see cref="RunDecision.End"/>, not <see cref="RunDecision.Park"/>, or the stop could not end the
/// run without resuming it first. <see cref="RunEvent.Continue"/>: it exists to add budget to a run
/// whose caps are spent. An accepted <c>phase: ping</c>: a run that is out of budget still presents.
///
/// The cap checks also require <see cref="RunState.Status"/> to be <see cref="RunStatus.Active"/>.
/// A parked or ended run's <c>SpawnsUsed</c>/<c>Elapsed</c> stay at or over a spent cap forever, so
/// without this guard a <see cref="RunEvent.HumanPosted"/> resume against any parked run would
/// re-Park here: a hard-capped park would re-Park instead of the one-line
/// <see cref="RunDecision.Refuse"/>, and a soft park would re-Park as a spent wall-clock cap purely
/// from having sat parked. A run that is not running cannot trip a cap again.
///
/// While the run has a job in flight, waiting is not a stall and not a silence: the stall park and
/// the conductor-silence re-ask do not fire, and <see cref="RunEvent.JobFinished"/> opens the
/// conductor. A job ends by itself or by its timeout, so the wait always ends.</summary>
public sealed class RunPolicy(RunLimits limits)
{
    /// <summary>The cap-spent refusal text, public so the service posts the identical line rather
    /// than duplicating the string.</summary>
    /// <summary>The end reason of a run whose conductor pinged: the run presented its result.</summary>
    public const string PingReason = "the conductor pinged";

    public const string CapSpentRefusal = "this run is parked because a cap is spent; /continue hours: N adds budget, or /stop it";

    public RunDecision Decide(RunState s, RunEvent e, IReadOnlyList<long> pendingSteers)
    {
        // A wake, a steer or a job finish while the conductor is still running lets its turn finish,
        // so a run out of budget still presents (each decides Nothing while the conductor runs). The
        // turn is bounded by the spawn timeout, and its post or its end is checked against the caps
        // like any other event.
        var passesCaps = e is RunEvent.StopRequested or RunEvent.Continue
            || e is RunEvent.ConductorPosted { Refusal: null, Tag.Kind: "ping" }
            || (e is RunEvent.Tick or RunEvent.HumanPosted or RunEvent.JobFinished && s.ConductorInFlight);
        if (!passesCaps && s.Status == RunStatus.Active)
        {
            if (s.SpawnsUsed >= s.SpawnBudget)
                return new RunDecision.Park($"the run used its {s.SpawnBudget} spawns", true);
            if (s.Elapsed >= s.WallClockBudget)
                return new RunDecision.Park($"the run passed {s.WallClockBudget} of active time", true);
        }

        return e switch
        {
            RunEvent.ConductorPosted p => DecideConductorPosted(s, p),
            RunEvent.ExchangeConcluded x => DecideExchangeConcluded(s, x, pendingSteers),
            RunEvent.SpawnSilent sp => DecideSpawnSilent(s, sp, pendingSteers),
            RunEvent.HumanPosted h => DecideHumanPosted(s, h, pendingSteers),
            RunEvent.StopRequested => new RunDecision.End("stopped by the owner", ExchangeStopCause.Owner),
            RunEvent.Tick => DecideTick(s, pendingSteers),
            RunEvent.Continue c => DecideContinue(s, c, pendingSteers),
            RunEvent.JobFinished j => DecideJobFinished(s, j, pendingSteers),
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

        if (tag.Kind == "ping") return new RunDecision.End(PingReason, ExchangeStopCause.Run);

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

    private RunDecision DecideSpawnSilent(RunState s, RunEvent.SpawnSilent sp, IReadOnlyList<long> pendingSteers)
    {
        if (sp.ParticipantId != s.ConductorId) return new RunDecision.Nothing();

        // The conductor ended its turn to wait on a job: JobFinished opens it again. A steer that
        // came in while it ran does not wait for the job.
        if (s.JobsInFlight > 0)
            return pendingSteers.Count > 0 ? new RunDecision.OpenConductor(s.RootMessageId, pendingSteers) : new RunDecision.Nothing();

        // A job note or steer that came in while the conductor ran rides the re-ask.
        IReadOnlyList<long> triggers = [.. sp.TriggerIds, .. pendingSteers];
        if (s.SilencesThisPhase == 0) return new RunDecision.OpenConductor(s.RootMessageId, triggers);

        var entries = s.PhaseEntries.GetValueOrDefault(s.Phase);
        if (entries >= limits.PhaseEntries) return new RunDecision.Park("the conductor did not post", false);

        // The service counts a phase entry for this ask.
        return new RunDecision.OpenConductor(s.RootMessageId, triggers);
    }

    private static RunDecision DecideHumanPosted(RunState s, RunEvent.HumanPosted h, IReadOnlyList<long> pendingSteers)
    {
        // The service records the steer.
        if (s.Status == RunStatus.Active) return new RunDecision.Nothing();

        if (s.Status == RunStatus.Parked)
        {
            if (s.CapSpent || s.OverBudget)
                return new RunDecision.Refuse(CapSpentRefusal);
            return new RunDecision.OpenConductor(s.RootMessageId, [h.MessageId, .. pendingSteers]);
        }

        // Ended, or any other status: nothing left to steer.
        return new RunDecision.Nothing();
    }

    private static RunDecision DecideTick(RunState s, IReadOnlyList<long> pendingSteers)
    {
        if (s.Status != RunStatus.Active || s.ExchangeOpen || s.AnythingInFlight) return new RunDecision.Nothing();

        if (pendingSteers.Count > 0) return new RunDecision.OpenConductor(s.RootMessageId, pendingSteers);
        // Waiting on a job is not a stall.
        return s.JobsInFlight > 0 ? new RunDecision.Nothing() : new RunDecision.Park("the run stalled", false);
    }

    /// <summary>A bare `/continue` on a soft park within budget resumes like any human post. Budget is
    /// added to a park that is cap-spent or over budget by its numbers, or when the post names
    /// `hours:`, with the default when it names none. The added hours count from the whole hours the
    /// run already used, when that passed the old budget, so the extended run never re-parks at once.</summary>
    private RunDecision DecideContinue(RunState s, RunEvent.Continue c, IReadOnlyList<long> pendingSteers)
    {
        if (s.Status == RunStatus.Active)
            return new RunDecision.Refuse($"A run is active in this room (#{s.RunId}); /continue hours: N extends a parked run.");
        if (s.Status != RunStatus.Parked) return new RunDecision.Nothing();
        if (c.Hours.Kind == HoursKind.Invalid) return new RunDecision.Refuse(RunBudget.RangeNote);
        IReadOnlyList<long> triggers = [c.MessageId, .. pendingSteers];
        if (!s.CapSpent && !s.OverBudget && c.Hours.Kind == HoursKind.None) return new RunDecision.OpenConductor(s.RootMessageId, triggers);

        var hours = c.Hours.Kind == HoursKind.Valid ? c.Hours.Value : limits.DefaultHours;
        // Floor: a wall-clock park always lands a moment past the deadline, and rounding that up would
        // grant an hour nobody asked for. Floor plus N (N >= 1) is still past what the run used.
        var usedHours = (int)Math.Floor(Max(s.WallClockBudget, s.Elapsed) / limits.Hour);
        var usedSpawns = Math.Max(s.SpawnBudget, s.SpawnsUsed);
        return new RunDecision.Extend(hours, usedHours + hours, usedSpawns + hours * limits.SpawnsPerHour, s.RootMessageId, triggers);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    /// <summary>An idle active run wakes its conductor with the job's note first. A busy one does
    /// nothing now: the service keeps the note id with the pending steers, so the next conductor
    /// spawn still gets it.</summary>
    private static RunDecision DecideJobFinished(RunState s, RunEvent.JobFinished j, IReadOnlyList<long> pendingSteers)
    {
        if (s.Status != RunStatus.Active || s.ExchangeOpen || s.AnythingInFlight) return new RunDecision.Nothing();
        var triggers = new List<long> { j.NoteMessageId };
        triggers.AddRange(pendingSteers);
        return new RunDecision.OpenConductor(s.RootMessageId, triggers);
    }
}
