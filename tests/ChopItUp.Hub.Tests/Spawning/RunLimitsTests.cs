using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>D9: the run ceiling is one hard-coded value, not configuration — no constructor argument,
/// no environment variable, no room-scoped override exists anywhere near it.</summary>
public sealed class RunLimitsTests
{
    [Fact]
    public void Default_carries_D9s_four_numbers()
    {
        Assert.Equal(80, RunLimits.Default.Spawns);
        Assert.Equal(TimeSpan.FromHours(8), RunLimits.Default.WallClock);
        Assert.Equal(TimeSpan.FromMinutes(30), RunLimits.Default.SpawnTimeout);
        Assert.Equal(3, RunLimits.Default.PhaseEntries);
    }

    // --- Row 20 task 3: GateTimeout, the ceiling run_gate and the MCP timeouts actually use --------

    [Fact]
    public void GateTimeout_is_below_SpawnTimeout_by_default_and_the_invariant_is_enforced()
    {
        Assert.Null(RunLimits.Default.GateTimeout);
        Assert.Equal(TimeSpan.FromMinutes(25), RunLimits.Default.EffectiveGateTimeout);
        Assert.True(RunLimits.Default.EffectiveGateTimeout < RunLimits.Default.SpawnTimeout);

        var explicitLimits = new RunLimits(Spawns: 80, WallClock: TimeSpan.FromHours(8), SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3, GateTimeout: TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), explicitLimits.EffectiveGateTimeout);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunLimits(Spawns: 80, WallClock: TimeSpan.FromHours(8), SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3, GateTimeout: TimeSpan.FromMinutes(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunLimits(Spawns: 80, WallClock: TimeSpan.FromHours(8), SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3, GateTimeout: TimeSpan.FromMinutes(45)));
    }

    [Fact]
    public void A_SpawnTimeout_too_small_to_leave_a_positive_default_GateTimeout_is_refused()
    {
        // No explicit GateTimeout: EffectiveGateTimeout defaults to SpawnTimeout - 5 min. At a 4-minute
        // SpawnTimeout that default is negative, which the old ">= SpawnTimeout" check alone missed
        // (-1 min is not >= 4 min).
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunLimits(Spawns: 1, WallClock: TimeSpan.FromHours(1), SpawnTimeout: TimeSpan.FromMinutes(4), PhaseEntries: 1));
    }
}
