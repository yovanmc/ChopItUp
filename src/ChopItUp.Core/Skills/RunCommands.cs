namespace ChopItUp.Core.Skills;

/// <summary>The one reserved command a run understands: `/stop`, which ends an active OR parked run
/// without ever resolving as a skill invocation. <see cref="StopName"/> is also what
/// <c>SkillImport</c> refuses to install a skill under, so an installed skill cannot shadow it. Both
/// sides read the same constant, so the two can never disagree on what is reserved.</summary>
public static class RunCommands
{
    public const string StopName = "stop";

    /// <summary>Reuses <see cref="SlashCommands.TryParse"/> rather than re-parsing the body, so
    /// `/stop` inherits the exact same "first line, `/` immediately followed by the name" shape a
    /// skill invocation gets - prose containing `/stop`, or a later line, is not a stop request, and
    /// trailing arguments after the name are ignored just like a skill's are.</summary>
    public static bool IsStop(string? body) =>
        SlashCommands.TryParse(body, out var command) && string.Equals(command.Name, StopName, StringComparison.Ordinal);
}
