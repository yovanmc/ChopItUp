using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Tests.Skills;

/// <summary>Row 19, task 13: the `/stop` reserved command. <see cref="RunCommands.IsStop"/> reuses
/// <see cref="SlashCommands.TryParse"/> (ticket 13) rather than re-parsing the body itself, so it
/// inherits the exact same "first line, `/` immediately followed by a name" shape - a `/stop` buried
/// in prose or on a later line is not a stop request, exactly as it is not a skill invocation.</summary>
public sealed class RunCommandsTests
{
    [Fact]
    public void A_bare_stop_invocation_is_recognised()
    {
        Assert.True(RunCommands.IsStop("/stop"));
    }

    [Fact]
    public void Stop_with_trailing_arguments_is_still_recognised()
    {
        Assert.True(RunCommands.IsStop("/stop please"));
    }

    [Fact]
    public void A_different_skill_name_is_not_a_stop_request()
    {
        Assert.False(RunCommands.IsStop("/build-thing @sonnet begin"));
    }

    [Fact]
    public void Stop_on_a_later_line_is_prose_not_a_stop_request()
    {
        Assert.False(RunCommands.IsStop("text\n/stop"));
    }

    [Fact]
    public void Null_or_empty_body_is_not_a_stop_request()
    {
        Assert.False(RunCommands.IsStop(null));
        Assert.False(RunCommands.IsStop(""));
    }

    [Fact]
    public void Uppercase_stop_is_not_recognised_same_as_any_other_slash_command()
    {
        Assert.False(RunCommands.IsStop("/Stop"));
    }
}
