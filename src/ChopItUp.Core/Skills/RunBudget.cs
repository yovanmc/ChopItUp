using System.Globalization;
using System.Text.RegularExpressions;
using ChopItUp.Core.Messaging;

namespace ChopItUp.Core.Skills;

/// <summary>What the `hours:` token of a run-start or `/continue` post said.</summary>
public enum HoursKind { None, Valid, Invalid }

public readonly record struct HoursToken(HoursKind Kind, int Value);

/// <summary>A run's budget in hours. The `hours: N` token is read at the point where the post's
/// leading run of mentions (<see cref="Mentions.LeadingEnd"/>) stops, so `/goal @opus hours: 3 ...`
/// reads 3 and an `hours:` inside prose is never read. A malformed value is reported, never clamped.
/// Each budget hour also buys <c>SpawnsPerHour</c> spawns (hub side).</summary>
public static class RunBudget
{
    public const int MinHours = 1;
    public const int MaxHours = 8;
    public const int DefaultHours = 4;
    public const string RangeNote = "hours: must be a whole number from 1 to 8 (default 4).";

    // Same shape as the `turns:` token in Mentions: once `hours:` is found it always matches, and the
    // digits and any junk glued to them are captured, so `2.5`, `3h` and `-1` are read and refused.
    private static readonly Regex Hours = new(@"\G[ \t\r\n\f\v,:;]*[Hh][Oo][Uu][Rr][Ss]:[ \t]*(?<n>[0-9]*)(?<junk>[A-Za-z0-9_.\-]*)", RegexOptions.CultureInvariant);

    public static HoursToken Read(string? body)
    {
        if (string.IsNullOrEmpty(body)) return new(HoursKind.None, 0);
        var text = body.Replace("\r\n", "\n");
        var start = SlashCommands.TryParse(text, out var command) ? 1 + command.Name.Length : 0;
        var h = Hours.Match(text, Mentions.LeadingEnd(text, start));
        if (!h.Success) return new(HoursKind.None, 0);
        var n = h.Groups["n"].Value;
        return h.Groups["junk"].Value.Length == 0 && n.Length is > 0 and <= 9
            && int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is >= MinHours and <= MaxHours
                ? new(HoursKind.Valid, value)
                : new(HoursKind.Invalid, 0);
    }
}
