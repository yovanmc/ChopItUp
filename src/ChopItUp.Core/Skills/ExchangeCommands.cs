namespace ChopItUp.Core.Skills;

/// <summary>What the `turns:` token in a message's leading run said: absent, a value in range, or a
/// value the hub refuses (out of range or zero). The reader never clamps: a wrong number is reported in
/// a note, and the caller applies the default.</summary>
public enum TurnsToken { None, Valid, OutOfRange }

/// <summary>The reserved `/continue` command (parsed through <see cref="SlashCommands"/> like
/// `/stop`, refused as a skill name by <c>SkillImport</c>) and the ceiling of the `turns: N` token that
/// <c>Mentions.Leading</c> reads as part of the leading run.</summary>
public static class ExchangeCommands
{
    public const string ContinueName = "continue";
    public const int MaxTurns = 16;

    public static bool IsContinue(string? body) =>
        SlashCommands.TryParse(body, out var command) && string.Equals(command.Name, ContinueName, StringComparison.Ordinal);
}
