using ChopItUp.Core.Model;

namespace ChopItUp.Core.Tests.Model;

/// <summary>Row 19 task 2c. Ticket 02's acceptance line, one case per fact/theory row.</summary>
public sealed class PhaseTagTests
{
    [Fact]
    public void A_tag_alone_parses()
    {
        Assert.True(PhaseTag.TryParse("phase: build", out var tag));
        Assert.Equal(("build", (string?)null), (tag!.Kind, tag.Name));
        Assert.Equal("build", tag.ToString());
    }

    [Fact]
    public void A_tag_followed_by_the_rest_of_the_sentence_parses_the_M8_regression()
    {
        Assert.True(PhaseTag.TryParse("phase: build @sonnet make hello.txt", out var tag));
        Assert.Equal("build", tag!.Kind);
        Assert.Null(tag.Name);
    }

    [Fact]
    public void A_name_narrows_the_kind_and_ToString_renders_kind_slash_name()
    {
        Assert.False(PhaseTag.TryParse("critique/pass-2", out var bare));   // no "phase:" prefix
        Assert.Null(bare);

        Assert.True(PhaseTag.TryParse("phase: critique/pass-2", out var tag));
        Assert.Equal(("critique", "pass-2"), (tag!.Kind, tag.Name));
        Assert.Equal("critique/pass-2", tag.ToString());
    }

    [Theory]
    [InlineData("phase:build")]                    // missing space
    [InlineData("phase: Build")]                    // wrong case
    [InlineData("phase: nonsense")]                  // unknown kind
    [InlineData("**phase: build**")]                 // wrapped in markdown emphasis
    [InlineData("intro line\nphase: build")]         // tag on the second line
    [InlineData("not a phase line at all")]
    public void Every_malformed_form_fails(string body)
    {
        Assert.False(PhaseTag.TryParse(body, out var tag));
        Assert.Null(tag);
    }

    [Fact]
    public void Artifact_returns_the_trimmed_remainder_of_the_first_artifact_line()
    {
        Assert.Equal("docs/PLAN.md", PhaseTag.Artifact("phase: critique/pass-1\nartifact:   docs/PLAN.md  \nmore text"));
        Assert.Null(PhaseTag.Artifact("phase: build\nno artifact line here"));
    }
}
