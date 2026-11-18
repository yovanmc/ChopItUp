using ChopItUp.Core.Model;

namespace ChopItUp.Core.Tests.Model;

public sealed class RunArtifactListTests
{
    [Fact]
    public void The_list_is_unchanged_at_the_limit_and_keeps_the_newest_above_it()
    {
        var start = DateTimeOffset.UnixEpoch;
        var atLimit = Enumerable.Range(0, RunArtifactList.MaxShown).Select(i => new RunArtifact($"f{i:000}", "sonnet", start.AddSeconds(i))).ToList();
        var (same, none) = RunArtifactList.Newest(atLimit);
        Assert.Same(atLimit, same);
        Assert.Equal(0, none);

        var above = atLimit.Append(new RunArtifact("zz-newest", "sonnet", start.AddHours(1))).Append(new RunArtifact("aa-newest", "sonnet", start.AddHours(1))).ToList();
        var (shown, omitted) = RunArtifactList.Newest(above);
        Assert.Equal(2, omitted);
        Assert.Equal(RunArtifactList.MaxShown, shown.Count);
        Assert.Equal("aa-newest", shown[0].Path);                 // path order
        Assert.Equal("zz-newest", shown[^1].Path);
        Assert.DoesNotContain(shown, a => a.Path is "f000" or "f001");   // the two oldest
    }
}
