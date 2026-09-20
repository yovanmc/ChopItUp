using ChopItUp.Core.Model;
using ChopItUp.Hub.Spawning;

namespace ChopItUp.Hub.Tests.Spawning;

/// <summary>Milestone 51: the one place the run-effort rule lives (row 19's AC7/D10), so the Roles
/// dialog can show what a row's classes earn without a second copy of the rule drifting from the
/// launch site. <see cref="EffortPolicy.ForClasses"/> is the configured half (classes only);
/// <see cref="EffortPolicy.AtLaunch"/> adds the runtime half (inside a run, or its conductor).</summary>
public sealed class EffortPolicyTests
{
    private static Participant Row(string id, string? classes) => new(id, id, "model", "claude", id, null, classes);

    [Fact]
    public void A_judge_class_row_is_configured_for_the_raised_effort()
    {
        Assert.Equal(EffortPolicy.Raised, EffortPolicy.ForClasses(Row("opus", "visible,judge")));
        Assert.Equal(EffortPolicy.Raised, EffortPolicy.ForClasses(Row("fable", "judge")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plumbing")]
    [InlineData("visible")]
    [InlineData("bogus")]
    public void Any_other_row_is_configured_for_no_flag(string? classes)
    {
        Assert.Null(EffortPolicy.ForClasses(Row("sonnet", classes)));
    }

    [Fact]
    public void The_raised_effort_is_high_never_xhigh_or_max()
    {
        Assert.Equal("high", EffortPolicy.Raised);
    }

    /// <summary>The launch-time matrix the spawner applies: nothing outside a run; inside one, the
    /// conductor and any judge-class row, nobody else.</summary>
    [Theory]
    [InlineData("judge", false, false, null)]
    [InlineData("judge", false, true, null)]
    [InlineData("judge", true, false, "high")]
    [InlineData("plumbing", true, false, null)]
    [InlineData("plumbing", true, true, "high")]
    [InlineData(null, true, true, "high")]
    [InlineData(null, true, false, null)]
    public void At_launch_only_an_in_run_conductor_or_judge_gets_the_flag(string? classes, bool inRun, bool conductor, string? expected)
    {
        Assert.Equal(expected, EffortPolicy.AtLaunch(Row("x", classes), inRun, conductor));
    }
}
