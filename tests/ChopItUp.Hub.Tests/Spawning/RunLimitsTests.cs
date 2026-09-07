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
}
