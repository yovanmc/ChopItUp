using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Tests.Skills;

public sealed class RunBudgetTests
{
    [Theory]
    [InlineData("/goal @opus hours: 3 make the thing", 3)]
    [InlineData("/goal @opus hours:1 go", 1)]
    [InlineData("/goal @opus HOURS: 8\nmake it", 8)]
    [InlineData("/goal @opus turns: 2 hours: 5 go", 5)]
    [InlineData("/continue hours: 2", 2)]
    [InlineData("/continue\r\nhours: 6", 6)]
    [InlineData("/goal hours: 3 @opus go", 3)]   // read where the walk stops; the mention after it is prose, so the start is refused for no conductor
    public void A_whole_number_from_1_to_8_after_the_leading_mentions_is_valid(string body, int hours) =>
        Assert.Equal(new HoursToken(HoursKind.Valid, hours), RunBudget.Read(body));

    [Theory]
    [InlineData("/goal @opus hours: 0 go")]
    [InlineData("/goal @opus hours: 9 go")]
    [InlineData("/goal @opus hours: -1 go")]
    [InlineData("/goal @opus hours: 2.5 go")]
    [InlineData("/goal @opus hours: 3h go")]
    [InlineData("/goal @opus hours: go")]
    [InlineData("/continue hours:")]
    [InlineData("/goal @opus hours: 1234567890 go")]
    public void A_value_outside_1_to_8_or_malformed_is_invalid_never_clamped(string body) =>
        Assert.Equal(new HoursToken(HoursKind.Invalid, 0), RunBudget.Read(body));

    [Theory]
    [InlineData("/goal @opus make the thing")]
    [InlineData("/goal @opus track the hours: 3 per day")]   // prose after the leading run
    [InlineData("/continue")]
    [InlineData("")]
    [InlineData(null)]
    public void No_hours_token_in_the_leading_run_reads_as_none(string? body) =>
        Assert.Equal(HoursKind.None, RunBudget.Read(body).Kind);
}
