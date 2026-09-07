using ChopItUp.Core.Model;

namespace ChopItUp.Core.Tests.Model;

public sealed class ParticipantClassesTests
{
    [Fact]
    public void Parse_orders_by_the_All_vocabulary_regardless_of_stored_order()
    {
        Assert.Equal(
            [ParticipantClasses.Plumbing, ParticipantClasses.Visible, ParticipantClasses.Judge],
            ParticipantClasses.Parse("judge,visible,plumbing"));
    }

    [Fact]
    public void Parse_collapses_duplicates()
    {
        Assert.Equal(
            [ParticipantClasses.Visible, ParticipantClasses.Judge],
            ParticipantClasses.Parse("judge,visible,judge,visible"));
    }

    [Fact]
    public void Parse_drops_a_token_outside_the_vocabulary_and_Unknown_reports_it_once()
    {
        Assert.Equal([ParticipantClasses.Visible], ParticipantClasses.Parse("visible,astra,astra"));
        Assert.Equal(["astra"], ParticipantClasses.Unknown("visible,astra,astra"));
    }

    [Fact]
    public void Parse_is_case_insensitive_and_tolerates_surrounding_whitespace()
    {
        Assert.Equal(
            [ParticipantClasses.Plumbing, ParticipantClasses.Judge],
            ParticipantClasses.Parse("  JUDGE ,  Plumbing  "));
    }

    [Fact]
    public void Parse_ignores_empty_segments_from_a_trailing_or_doubled_comma()
    {
        Assert.Equal([ParticipantClasses.Plumbing], ParticipantClasses.Parse("plumbing,,"));
        Assert.Equal([ParticipantClasses.Plumbing], ParticipantClasses.Parse(",plumbing,"));
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
