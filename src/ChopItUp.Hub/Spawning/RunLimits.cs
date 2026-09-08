namespace ChopItUp.Hub.Spawning;

/// <summary>D9: the run caps are hard code, not configuration — exactly the shape
/// <see cref="SpawnLimits.Default"/> already sets for exchanges, and just as unreachable from inside a
/// room. <see cref="Spawns"/>, <see cref="WallClock"/> and <see cref="PhaseEntries"/> are the three
/// HARD caps (AC8): a park naming one of them is always <c>capSpent: true</c>.
/// <see cref="SpawnTimeout"/> is the per-spawn wall clock while a run is active, replacing
/// <see cref="SpawnLimits.Timeout"/>'s 5 minutes for the duration of a run (AC8's last clause).
///
/// <see cref="GateTimeout"/> (row 20, task 3; plan critique fold M4) is a trailing OPTIONAL parameter —
/// the ten <c>RunLimits</c> construction sites in <c>SpawnerServiceTests.Runs.cs</c> use named
/// arguments and never set it. <see cref="EffectiveGateTimeout"/> is what every consumer (the MCP
/// tool-call timeouts, <c>run_gate</c>'s own process timeout) actually reads: an explicit
/// <see cref="GateTimeout"/> when given, else <see cref="SpawnTimeout"/> minus a 5-minute reserve.
/// That reserve is what lets a conductor or worker still post a reply after a gate that ran all the
/// way to its own ceiling, rather than being killed by the spawn's own wall clock at the same instant
/// the gate call returns (ledger 24, 25) — so the invariant <c>EffectiveGateTimeout &lt; SpawnTimeout</c>
/// is enforced in the constructor, never left to a caller to get right.</summary>
public sealed record RunLimits(int Spawns, TimeSpan WallClock, TimeSpan SpawnTimeout, int PhaseEntries, TimeSpan? GateTimeout = null)
{
    public static readonly RunLimits Default = new(
        Spawns: 80, WallClock: TimeSpan.FromHours(8),
        SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3);

    // Redeclared (not left as the plain synthesized property) so the invariant below runs as part of
    // construction: a positional record's primary constructor cannot carry a validating body of its
    // own (measured this session - the "public RunLimits { ... }" idiom does not parse on this
    // compiler), but an initializer on a redeclared property DOES run at construction time, reading
    // the primary constructor's OWN parameters (SpawnTimeout, GateTimeout), not the properties -
    // ordinary `with` expressions bypass it (a documented record limitation; nothing in this codebase
    // ever `with`s a RunLimits), but every `new RunLimits(...)` call site - the only shape this repo
    // or its tests use - goes through it.
    public TimeSpan? GateTimeout { get; init; } = ValidatedGateTimeout(SpawnTimeout, GateTimeout);

    public TimeSpan EffectiveGateTimeout => GateTimeout ?? SpawnTimeout - TimeSpan.FromMinutes(5);

    private static TimeSpan? ValidatedGateTimeout(TimeSpan spawnTimeout, TimeSpan? gateTimeout)
    {
        var effective = gateTimeout ?? spawnTimeout - TimeSpan.FromMinutes(5);
        if (effective >= spawnTimeout)
            throw new ArgumentOutOfRangeException(nameof(gateTimeout), gateTimeout,
                $"EffectiveGateTimeout ({effective}) must leave headroom under SpawnTimeout ({spawnTimeout}) so a model can still post after a gate that ran to its own ceiling.");
        return gateTimeout;
    }
}
