using ChopItUp.Hub.Skills;

namespace ChopItUp.Hub.Spawning;

public enum ExchangeStatus { Open, Concluded, Superseded, Stopped }

/// <summary>A participant waiting to be launched, with every message that asked for it since the
/// last launch (one burst = one spawn, D8) and when the last of them arrived (the debounce clock).</summary>
public sealed class PendingSpawn
{
    public List<long> TriggerIds { get; } = new();
    public DateTimeOffset LastTriggerAt { get; set; }
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
    public required int Budget { get; init; }
    public ExchangeStatus Status { get; set; } = ExchangeStatus.Open;
    public int TurnsCommitted { get; set; }
    public int TurnsStarted { get; set; }
    public OrderedDictionary<string, PendingSpawn> Pending { get; } = new(StringComparer.Ordinal);
    public HashSet<string> InFlight { get; } = new(StringComparer.Ordinal);

    /// <summary>The skill in force for every spawn of this exchange (row 11, D-b): set once when the
    /// exchange opens and never changed, so turn 4 answers the same instruction as turn 1.</summary>
    public ResolvedSkill? Skill { get; init; }
}

/// <summary>What the service launches: who, why (the trigger ids), which exchange, and the two
/// numbers the prompt states.</summary>
public sealed record SpawnRequest(string RoomId, string ParticipantId, IReadOnlyList<long> TriggerIds, long RootMessageId, int TurnNumber, int RemainingAfter);
