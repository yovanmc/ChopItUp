using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Tests.Skills;

public sealed class SlashCommandTests
{
    [Fact]
    public void Bare_name_yields_empty_arguments()
    {
        Assert.True(SlashCommands.TryParse("/grill", out var cmd));
        Assert.Equal("grill", cmd.Name);
        Assert.Equal("", cmd.Arguments);
    }

    [Fact]
    public void Name_with_mentions_and_text_yields_the_rest_of_the_line_as_arguments()
    {
        Assert.True(SlashCommands.TryParse("/grill @opus what about X", out var cmd));
        Assert.Equal("grill", cmd.Name);
        Assert.Equal("@opus what about X", cmd.Arguments);
    }

    [Fact]
    public void Only_the_first_line_is_consulted_even_when_a_second_line_follows()
    {
        Assert.True(SlashCommands.TryParse("/grill\nsecond line", out var cmd));
        Assert.Equal("grill", cmd.Name);
        Assert.Equal("", cmd.Arguments);
    }

    [Fact]
    public void Leading_whitespace_before_the_slash_is_not_an_invocation()
    {
        Assert.False(SlashCommands.TryParse(" /grill", out _));
    }

    [Fact]
    public void A_second_slash_is_not_a_name()
    {
        Assert.False(SlashCommands.TryParse("//x", out _));
    }

    [Fact]
    public void A_space_right_after_the_slash_is_not_a_name()
    {
        Assert.False(SlashCommands.TryParse("/ x", out _));
    }

    [Fact]
    public void Uppercase_names_do_not_match()
    {
        Assert.False(SlashCommands.TryParse("/Grill", out _));
    }

    [Fact]
    public void A_name_cannot_start_with_a_hyphen()
    {
        Assert.False(SlashCommands.TryParse("/-x", out _));
    }

    [Fact]
    public void A_name_over_64_characters_does_not_match()
    {
        var name = new string('a', 65);
        Assert.False(SlashCommands.TryParse("/" + name, out _));
    }

    [Fact]
    public void A_slash_word_on_a_later_line_is_prose_not_an_invocation()
    {
        Assert.False(SlashCommands.TryParse("text\n/grill", out _));
    }

    [Fact]
    public void A_trailing_CRLF_after_the_bare_name_still_matches()
    {
        Assert.True(SlashCommands.TryParse("/grill\r\n", out var cmd));
        Assert.Equal("grill", cmd.Name);
        Assert.Equal("", cmd.Arguments);
    }

    [Fact]
    public void Null_or_empty_body_does_not_match()
    {
        Assert.False(SlashCommands.TryParse(null, out _));
        Assert.False(SlashCommands.TryParse("", out _));
    }
}
