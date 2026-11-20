using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>The run limits are one hard-coded value, not configuration: no environment variable and
/// no room-scoped override exists anywhere near them. A run's own budget is set only by a human
/// `hours:` token, in whole budget hours.</summary>
public sealed class RunLimitsTests
{
    [Fact]
    public void Default_carries_the_budget_hour_and_the_two_fixed_limits()
    {
        Assert.Equal(10, RunLimits.Default.SpawnsPerHour);
        Assert.Equal(TimeSpan.FromHours(1), RunLimits.Default.Hour);
        Assert.Equal(4, RunLimits.Default.DefaultHours);
        Assert.Equal(TimeSpan.FromHours(3), RunLimits.Default.WallClockFor(3));
        Assert.Equal(TimeSpan.FromMinutes(30), RunLimits.Default.SpawnTimeout);
        Assert.Equal(3, RunLimits.Default.PhaseEntries);
    }

    // --- GateTimeout, the ceiling run_gate and the MCP timeouts actually use -----------------------

    [Fact]
    public void GateTimeout_is_below_SpawnTimeout_by_default_and_the_invariant_is_enforced()
    {
        Assert.Null(RunLimits.Default.GateTimeout);
        Assert.Equal(TimeSpan.FromMinutes(25), RunLimits.Default.EffectiveGateTimeout);
        Assert.True(RunLimits.Default.EffectiveGateTimeout < RunLimits.Default.SpawnTimeout);

        var explicitLimits = new RunLimits(SpawnsPerHour: 80, Hour: TimeSpan.FromHours(8), DefaultHours: 1, SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3, GateTimeout: TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), explicitLimits.EffectiveGateTimeout);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunLimits(SpawnsPerHour: 80, Hour: TimeSpan.FromHours(8), DefaultHours: 1, SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3, GateTimeout: TimeSpan.FromMinutes(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunLimits(SpawnsPerHour: 80, Hour: TimeSpan.FromHours(8), DefaultHours: 1, SpawnTimeout: TimeSpan.FromMinutes(30), PhaseEntries: 3, GateTimeout: TimeSpan.FromMinutes(45)));
    }

    [Fact]
    public void A_SpawnTimeout_too_small_to_leave_a_positive_default_GateTimeout_is_refused()
    {
        // No explicit GateTimeout: EffectiveGateTimeout defaults to SpawnTimeout - 5 min. At a 4-minute
        // SpawnTimeout that default is negative, which a ">= SpawnTimeout" check alone misses
        // (-1 min is not >= 4 min).
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunLimits(SpawnsPerHour: 1, Hour: TimeSpan.FromHours(1), DefaultHours: 1, SpawnTimeout: TimeSpan.FromMinutes(4), PhaseEntries: 1));
    }
}
