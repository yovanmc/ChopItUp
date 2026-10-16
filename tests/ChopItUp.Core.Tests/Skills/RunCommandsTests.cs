using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Tests.Skills;

/// <summary>The `/stop` reserved command. <see cref="RunCommands.IsStop"/> reuses
/// <see cref="SlashCommands.TryParse"/> rather than re-parsing the body itself, so it inherits the
/// exact same "first line, `/` immediately followed by a name" shape - a `/stop` buried in prose or
/// on a later line is not a stop request, exactly as it is not a skill invocation.</summary>
public sealed class RunCommandsTests
{
    [Theory]
    [InlineData("/stop", true)]
    [InlineData("/stop please", true)]
    [InlineData("/build-thing @sonnet begin", false)]   // a different skill name
    [InlineData("text\n/stop", false)]                   // prose on an earlier line
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("/Stop", false)]                         // uppercase, same as any other slash command
    public void Only_a_first_line_lowercase_stop_invocation_is_a_stop_request(string? body, bool expected)
    {
        Assert.Equal(expected, RunCommands.IsStop(body));
    }
}
