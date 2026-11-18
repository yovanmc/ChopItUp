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
}
