using ChopItUp.Core.Model;

namespace ChopItUp.Core.Tests.Model;

public sealed class ParticipantClassesTests
{
    [Theory]
    // ordered by the All vocabulary regardless of stored order
    [InlineData("judge,visible,plumbing", new[] { ParticipantClasses.Plumbing, ParticipantClasses.Visible, ParticipantClasses.Judge })]
    // duplicates collapse
    [InlineData("judge,visible,judge,visible", new[] { ParticipantClasses.Visible, ParticipantClasses.Judge })]
    // case-insensitive and tolerant of surrounding whitespace
    [InlineData("  JUDGE ,  Plumbing  ", new[] { ParticipantClasses.Plumbing, ParticipantClasses.Judge })]
    // empty segments from a trailing or doubled comma are ignored
    [InlineData("plumbing,,", new[] { ParticipantClasses.Plumbing })]
    [InlineData(",plumbing,", new[] { ParticipantClasses.Plumbing })]
    public void Parse_orders_by_the_All_vocabulary_collapses_duplicates_and_tolerates_case_whitespace_and_empty_segments(string stored, string[] expected)
    {
        Assert.Equal(expected, ParticipantClasses.Parse(stored));
    }

    [Fact]
    public void Parse_drops_a_token_outside_the_vocabulary_and_Unknown_reports_it_once()
    {
        Assert.Equal([ParticipantClasses.Visible], ParticipantClasses.Parse("visible,astra,astra"));
        Assert.Equal(["astra"], ParticipantClasses.Unknown("visible,astra,astra"));
    }

    [Fact]
    public void Parse_and_Unknown_return_empty_for_null_and_blank_stored_values()
    {
        Assert.Empty(ParticipantClasses.Parse(null));
        Assert.Empty(ParticipantClasses.Parse(""));
        Assert.Empty(ParticipantClasses.Parse("   "));
        Assert.Empty(ParticipantClasses.Unknown(null));
        Assert.Empty(ParticipantClasses.Unknown(""));
        Assert.Empty(ParticipantClasses.Unknown("   "));
    }

    [Fact]
    public void Has_reports_whether_a_participant_carries_a_class()
    {
        var judge = new Participant("opus", "Opus", "model", "local", "opus", null, "visible,judge");
        var plainHuman = new Participant("owner", "Owner", "human", "local", null, null, null);

        Assert.True(ParticipantClasses.Has(judge, ParticipantClasses.Judge));
        Assert.False(ParticipantClasses.Has(judge, ParticipantClasses.Plumbing));
        Assert.False(ParticipantClasses.Has(plainHuman, ParticipantClasses.Visible));
    }
}
