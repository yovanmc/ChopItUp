using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Tests.Skills;

public sealed class SlashCommandTests
{
    [Theory]
    [InlineData("/grill", "grill", "")]
    [InlineData("/grill @opus what about X", "grill", "@opus what about X")]
    [InlineData("/grill\nsecond line", "grill", "")]   // only the first line is consulted
    [InlineData("/grill\r\n", "grill", "")]            // a trailing CRLF after the bare name still matches
    public void A_first_line_slash_name_parses_with_the_rest_of_the_line_as_arguments(string body, string name, string arguments)
    {
        Assert.True(SlashCommands.TryParse(body, out var cmd));
        Assert.Equal(name, cmd.Name);
        Assert.Equal(arguments, cmd.Arguments);
    }

    [Theory]
    [InlineData(" /grill")]        // leading whitespace before the slash
    [InlineData("//x")]            // a second slash is not a name
    [InlineData("/ x")]            // a space right after the slash is not a name
    [InlineData("/Grill")]         // uppercase names do not match
    [InlineData("/-x")]            // a name cannot start with a hyphen
    [InlineData("/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]            // a name over 64 characters does not match
    [InlineData("text\n/grill")]   // a slash word on a later line is prose
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_an_invocation(string? body)
    {
        Assert.False(SlashCommands.TryParse(body, out _));
    }
}
