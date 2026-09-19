using ChopItUp.Core.Messaging;
using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Tests.Skills;

public sealed class ExchangeCommandsTests
{
    private static readonly Mentions Reader = new(["opus", "sonnet"]);

    [Theory]
    [InlineData("turns: 3 @opus", TurnsToken.Valid, 3)]
    [InlineData("@opus turns:16 go", TurnsToken.Valid, 16)]
    [InlineData("Turns: 1", TurnsToken.Valid, 1)]
    [InlineData("/continue turns: 4", TurnsToken.Valid, 4)]
    [InlineData("/continue @sonnet turns: 2 more", TurnsToken.Valid, 2)]
    [InlineData("@opus, @sonnet: turns: 6 go", TurnsToken.Valid, 6)]
    [InlineData("@opus,\nturns: 3 @sonnet", TurnsToken.Valid, 3)]
    [InlineData("turns: 0 @opus", TurnsToken.OutOfRange, 0)]
    [InlineData("turns: 17 @opus", TurnsToken.OutOfRange, 0)]
    [InlineData("turns: 999 @opus", TurnsToken.OutOfRange, 0)]
    [InlineData("turns: 1000000000 @opus", TurnsToken.OutOfRange, 0)]   // I-M3: 10 digits, refused before int.TryParse ever sees it
    [InlineData("@opus go\nturns: 3", TurnsToken.None, 0)]
    [InlineData("@opus xturns: 3", TurnsToken.None, 0)]
    // I-M3 (hub F3): the tolerant regex always matches once `turns:` is found, so extra whitespace
    // before the digits is now read (Valid), and a letter glued to the digits is now refused
    // (OutOfRange) rather than both silently breaking the walk into None the way the old, stricter
    // regex did.
    [InlineData("@opus turns:  3", TurnsToken.Valid, 3)]
    [InlineData("@opus turns: 3x", TurnsToken.OutOfRange, 0)]
    [InlineData("@opus how many turns: 16 did we burn?", TurnsToken.None, 0)]
    [InlineData("/Grill turns: 3", TurnsToken.None, 0)]
    [InlineData("turns: 2 turns: 9", TurnsToken.Valid, 2)]
    [InlineData("", TurnsToken.None, 0)]
    public void Turns_token_is_read_in_the_leading_run_first_wins_and_never_clamped(string body, TurnsToken expected, int value)
    {
        var leading = Reader.Leading(body);
        Assert.Equal(expected, leading.Turns);
        Assert.Equal(value, leading.TurnsValue);
    }

    [Theory]
    [InlineData("/continue", true)]
    [InlineData("/continue @sonnet more", true)]
    [InlineData("/continue\nsecond line", true)]
    [InlineData("please /continue", false)]
    [InlineData("/continued", false)]
    [InlineData("x\n/continue", false)]
    [InlineData(null, false)]
    public void Continue_is_the_first_line_slash_form_only(string? body, bool expected) =>
        Assert.Equal(expected, ExchangeCommands.IsContinue(body));
}
