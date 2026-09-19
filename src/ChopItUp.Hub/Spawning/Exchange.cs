using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Spawning;

public enum ExchangeStatus { Open, Concluded, Superseded, Stopped }

/// <summary>Who caused a <see cref="ExchangeStatus.Stopped"/> exchange to stop (row 27): decided by
/// whichever policy raised the stop and passed down to <see cref="ExchangePolicy.Stop"/>, never
/// inferred from the reason string (P7 - the service never decides).</summary>
public enum ExchangeStopCause { Owner, Run }

/// <summary>Why a spawn is queued (row 44): a mention (the ordinary case), the addressee's synthesis
/// turn the hub queued itself, or a turn /continue re-queued.</summary>
public enum SpawnReason { Mention, Synthesis, Continuation }

/// <summary>A participant waiting to be launched, with every message that asked for it since the
/// last launch (one burst = one spawn, D8) and when the last of them arrived (the debounce clock).</summary>
public sealed class PendingSpawn
{
    public List<long> TriggerIds { get; } = new();
    public DateTimeOffset LastTriggerAt { get; set; }
    public SpawnReason Reason { get; init; } = SpawnReason.Mention;
}

/// <summary>One room's exchange (D5): rooted in an owner message, a budget of model turns, then a
/// conclusion. Mutable, owned by the <c>SpawnerService</c> loop — one thread touches it, so no
/// locks. It is not persisted (plan decision 3); the room's messages are the durable trail.
/// <see cref="TurnsCommitted"/> counts accepted mentions (pending or launched); <see cref="TurnsStarted"/>
/// counts launches. <see cref="Pending"/> keeps insertion order — that IS mention order (A1).</summary>
public sealed class Exchange
{
    public required string RoomId { get; init; }
    public required long RootMessageId { get; init; }

    /// <summary>Row 44: set at open, raised by /continue and by a synthesis turn that found no free turn.</summary>
    public required int Budget { get; set; }
    public ExchangeStatus Status { get; set; } = ExchangeStatus.Open;

    /// <summary>Row 27: who caused the stop, set by <see cref="ExchangePolicy.Stop"/> when
    /// <see cref="Status"/> becomes <see cref="ExchangeStatus.Stopped"/>; null until then.</summary>
    public ExchangeStopCause? StopCause { get; set; }
    public int TurnsCommitted { get; set; }
    public int TurnsStarted { get; set; }
    public OrderedDictionary<string, PendingSpawn> Pending { get; } = new(StringComparer.Ordinal);
    public HashSet<string> InFlight { get; } = new(StringComparer.Ordinal);

    /// <summary>Every participant this exchange ever accepted a turn for, launched or still pending.
    /// An owner prompt that mentions one of them supersedes the exchange; a prompt that mentions none of
    /// them runs beside it.</summary>
    public HashSet<string> Participants { get; } = new(StringComparer.Ordinal);

    /// <summary>Row 36: the messages that belong to this exchange, so an owner reply to any of them joins
    /// it. Its root, every owner reply that joined it, every post of its own spawns and every app-backed
    /// model post routed to it while open. Hub notes are never members.</summary>
    public HashSet<long> MessageIds { get; } = new();

    /// <summary>Row 36: opened by an owner prompt that was not a run-start. Only such an exchange can be
    /// joined by a reply; a run's own exchanges never can.</summary>
    public bool Joinable { get; init; }

    /// <summary>The skill in force for every spawn of this exchange (row 11, D-b): set once when the
    /// exchange opens and never changed, so turn 4 answers the same instruction as turn 1.</summary>
    public ResolvedSkill? Skill { get; init; }

    /// <summary>Row 35: the room directory whose worktree this exchange used, set at its first
    /// worktree launch; null for an exchange that never launched in a worktree.</summary>
    public string? WorktreeRoom { get; set; }

    /// <summary>Row 35: a spawn of this exchange ran in a worktree it was really given (not a refused
    /// lease).</summary>
    public bool WorktreeLeased { get; set; }

    /// <summary>Row 35: a spawn of this exchange was cancelled or timed out, so its tree may be
    /// half-written and a close keeps the branch unmerged whatever the status says.</summary>
    public bool Interrupted { get; set; }

    /// <summary>Row 36: reopened while a worktree close was running in its room, which may be its own;
    /// it launches nothing until that close has finished.</summary>
    public bool WaitsForClose { get; set; }

    /// <summary>Row 36: reopened after its worktree was handed to a close, so its next lease may continue
    /// the branch that close kept instead of refusing it.</summary>
    public bool ContinuesBranch { get; set; }

    /// <summary>Row 44: the first spawnable participant the root message addressed, for an
    /// exchange an owner prompt opened outside a run; null for run and conductor exchanges. Gets one
    /// synthesis turn when another participant's post would otherwise have been the last.</summary>
    public string? Addressee { get; init; }

    /// <summary>Row 44: hand-offs the budget refused, in refusal order, each with the message that made
    /// it; what /continue re-queues when nobody was named. Cleared by a continue, and by a reply
    /// that reopened the exchange and was accepted.</summary>
    public OrderedDictionary<string, long> Refused { get; } = new(StringComparer.Ordinal);

    /// <summary>Row 44: a synthesis turn was queued in this leg; a second one never is. Reset by a
    /// continue or an accepted reopening reply.</summary>
    public bool SynthesisUsed { get; set; }

    /// <summary>Row 44: the queued synthesis found no free turn and grew the budget by one; cleared when
    /// it launches, and undone by a stop or supersede that drops it while still pending.</summary>
    public bool SynthesisGrewBudget { get; set; }

    /// <summary>Row 44: the last model post that landed in this exchange (author and id), read by
    /// <see cref="ExchangePolicy.Finished"/> to decide whether the addressee still owes a wrap-up.</summary>
    public (string AuthorId, long MessageId)? LastModelPost { get; set; }
}

/// <summary>What the service launches: who, why (the trigger ids), which exchange, and the two
/// numbers the prompt states.</summary>
public sealed record SpawnRequest(string RoomId, string ParticipantId, IReadOnlyList<long> TriggerIds, long RootMessageId, int TurnNumber, int RemainingAfter, SpawnReason Reason = SpawnReason.Mention);
