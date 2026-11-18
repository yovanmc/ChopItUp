using ChopItUp.Core.Model;
using ChopItUp.Core.Storage;

namespace ChopItUp.Core.Tests.Model;

public sealed class ArtifactRefTests
{
    [Theory]
    [InlineData("artifact:x.png", "x.png")]
    [InlineData("artifact:renders/round-1/x.png", "renders/round-1/x.png")]
    [InlineData(@"artifact:renders\x.png", "renders/x.png")]
    [InlineData("ARTIFACT:x.png", "x.png")]
    [InlineData("artifact:a~b.png", "a~b.png")]
    public void A_canonical_reference_gives_its_path_inside_the_folder(string value, string expected)
    {
        Assert.True(ArtifactRef.TryRelative(value, out var relative));
        Assert.Equal(expected, relative);
    }

    [Theory]
    [InlineData("x.png")]                       // a repo path, not a reference
    [InlineData("artifact:")]
    [InlineData("artifact:/x.png")]             // rooted
    [InlineData(@"artifact:C:\x.png")]
    [InlineData("artifact:../x.png")]
    [InlineData("artifact:a/../../x.png")]
    [InlineData("artifact:x.png:stream")]       // an alternate data stream
    [InlineData("artifact:x.png.")]             // Windows resolves each of these to x.png
    [InlineData("artifact:x.png ")]
    [InlineData("artifact:a//x.png")]
    [InlineData("artifact:a/./x.png")]
    [InlineData("artifact:LONGNA~1.PNG")]       // an 8.3 short name
    [InlineData("artifact:x*.png")]
    public void A_reference_with_no_single_spelling_is_refused(string value) =>
        Assert.False(ArtifactRef.TryRelative(value, out _));

    [Theory]
    [InlineData("artifact:./Renders/X.png")]
    [InlineData("`artifact:renders/x.png`")]
    [InlineData(@"artifact:renders\x.png")]
    public void Spellings_of_one_reference_normalize_to_the_key_authorship_is_recorded_under(string spelling) =>
        Assert.Equal("artifact:renders/x.png", RunStore.Normalize(spelling));

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
