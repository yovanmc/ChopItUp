using ChopItUp.Core.Messaging;

namespace ChopItUp.Core.Tests.Messaging;

public sealed class MentionsTests
{
    private static readonly string[] Ids = ["owner", "claude", "codex", "opus", "sonnet", "gpt-6-astra", "gpt-5.6-sol", "gpt-5.5", "hub"];

    [Fact]
    public void Finds_ids_in_first_appearance_order_without_duplicates()
    {
        var m = new Mentions(Ids);
        Assert.Equal(["opus", "gpt-5.6-sol", "owner"], m.Find("@opus then @gpt-5.6-sol, and @opus again; back to @owner."));
    }

    [Fact]
    public void Is_case_insensitive_and_reports_the_canonical_id()
    {
        var m = new Mentions(Ids);
        Assert.Equal(["opus", "gpt-6-astra"], m.Find("@Opus @GPT-6-ASTRA"));
    }

    [Fact]
    public void Sentence_final_punctuation_still_matches_but_a_longer_word_does_not()
    {
        var m = new Mentions(Ids);
        Assert.Equal(["claude"], m.Find("ask @claude."));
        Assert.Empty(m.Find("ask @claude-2 or @claude.x or @claudette"));
    }

    [Fact]
    public void Dotted_ids_win_over_their_prefixes()
    {
        var m = new Mentions(Ids);
        Assert.Equal(["gpt-5.6-sol"], m.Find("@gpt-5.6-sol please"));
        Assert.Equal(["gpt-5.5"], m.Find("@gpt-5.5 please"));
    }

    [Fact]
    public void An_email_address_is_not_a_mention()
    {
        var m = new Mentions(Ids);
        Assert.Empty(m.Find("mail me at me@opus.com"));
        Assert.Empty(m.Find("x-@sonnet"));
    }

    [Fact]
    public void Unknown_ids_and_empty_bodies_yield_nothing()
    {
        var m = new Mentions(Ids);
        Assert.Empty(m.Find("@nobody @ owner"));
        Assert.Empty(m.Find(""));
        Assert.Empty(new Mentions([]).Find("@opus"));
    }
}
