using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

public sealed class SpawnLimitsTests
{
    [Fact]
    public void D7_caps_are_the_grilled_values_and_are_not_configuration()
    {
        var d = SpawnLimits.Default;
        Assert.Equal((4, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), 60, 24_000),
            (d.Budget, d.Debounce, d.MinSpacing, d.Timeout, d.TranscriptMessages, d.TranscriptChars));
    }
}
