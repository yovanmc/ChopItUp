using System.Text.RegularExpressions;

namespace ChopItUp.Core.Skills;

/// <summary>One skill invocation parsed out of a message body: the name and everything the owner
/// typed after it. Mentions are NOT stripped — <c>Mentions.Find</c> still runs over the whole body,
/// so `/grill @opus @gpt-6-astra what about X` invokes grill AND spawns both rows.</summary>
public sealed record SlashCommand(string Name, string Arguments);

/// <summary>The slash form: the message's FIRST line, `/` immediately followed by a skill name, then
/// end-of-line or whitespace. Deliberately narrow — a line beginning `/home/user` or `//` or `/ x` is
/// not an invocation, and a `/word` anywhere but the first line is prose. Only the caller decides who
/// may invoke; this class does not know about participants.</summary>
public static class SlashCommands
{
    /// <summary>Same shape as a skill directory name (see <c>SkillStore.NamePattern</c>): lowercase,
    /// digits and hyphens, starting with an alphanumeric, at most 64 characters.</summary>
    private static readonly Regex Pattern = new(
        @"^/(?<name>[a-z0-9][a-z0-9-]{0,63})(?:[ \t]+(?<args>[^\r\n]*))?(?=\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? body, out SlashCommand command)
    {
        command = new SlashCommand("", "");
        if (string.IsNullOrEmpty(body)) return false;
        var match = Pattern.Match(body);
        if (!match.Success) return false;
        command = new SlashCommand(match.Groups["name"].Value, match.Groups["args"].Value.Trim());
        return true;
    }
}
