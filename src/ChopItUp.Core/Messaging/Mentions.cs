using System.Text.RegularExpressions;
using ChopItUp.Core.Model;
using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Messaging;

/// <summary>Server-side mention detection, built once per roster. The shape mirrors the client's
/// highlighter (<c>participants.ts</c>, M8): <c>@</c> + id, not followed by an optional dot and a
/// word character, so <c>@claude.</c> at the end of a sentence matches and <c>@claude-2</c> does
/// not. The server adds a lookbehind the client lacks — <c>me@opus.com</c> may highlight in the
/// browser but must never spawn (plan decision 5). Ids are matched longest-first so a dotted id
/// beats its prefix, case-insensitively, and reported by canonical id in first-appearance order
/// without duplicates.</summary>
public sealed class Mentions
{
    private readonly Regex? _pattern;
    private readonly Dictionary<string, string> _canonical;

    public Mentions(IEnumerable<string> ids)
    {
        _canonical = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(i => i, i => i, StringComparer.OrdinalIgnoreCase);
        if (_canonical.Count == 0) return;
        var alternation = string.Join("|", _canonical.Keys.OrderByDescending(i => i.Length).ThenBy(i => i, StringComparer.Ordinal).Select(Regex.Escape));
        _pattern = new Regex($@"(?<![\w-])@({alternation})(?!\.?[\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public IReadOnlyList<string> Find(string body)
    {
        if (_pattern is null || string.IsNullOrEmpty(body)) return [];
        var found = new List<string>();
        foreach (Match m in _pattern.Matches(body))
        {
            var id = _canonical[m.Groups[1].Value];
            if (!found.Contains(id)) found.Add(id);
        }
        return found;
    }

    /// <summary>Row 43 (D5): who a message addresses. Only the run of @word tokens at the start of the
    /// body counts — after an optional command prefix, the <c>/name</c> token <see cref="SlashCommands"/>
    /// recognises or the <c>phase:</c> tag <see cref="PhaseTag"/> recognises. Tokens are separated by
    /// ASCII whitespace (line breaks included), commas, colons or semicolons; a trailing sentence mark on
    /// a word is not part of the id. Recipients are canonical roster ids in first-appearance order without
    /// duplicates; Unknown are the leading words that matched nobody, verbatim. Everything after the first
    /// non-@ token is prose, and <see cref="Find"/> still sees it as a reference. Character classes are
    /// spelled out in ASCII (never <c>\w</c>/<c>\s</c>) so the client's twin in participants.ts, whose
    /// engine defines those classes differently, reads every body the same way.</summary>
    public sealed record LeadingMentions(IReadOnlyList<string> Recipients, IReadOnlyList<string> Unknown)
    {
        public static readonly LeadingMentions None = new([], []);
    }

    private static readonly Regex Token = new(@"\G[ \t\r\n\f\v,:;]*@(?<word>[A-Za-z0-9][A-Za-z0-9_.\-]*)(?![\p{L}\p{N}_.\-\uD800-\uDBFF])", RegexOptions.CultureInvariant);

    public LeadingMentions Leading(string body)
    {
        if (string.IsNullOrEmpty(body)) return LeadingMentions.None;
        var text = body.Replace("\r\n", "\n");
        var start = 0;
        if (SlashCommands.TryParse(text, out var command)) start = 1 + command.Name.Length;
        else if (PhaseTag.TryParse(text, out _, out var tagLength)) start = tagLength;
        var recipients = new List<string>();
        var unknown = new List<string>();
        for (var m = Token.Match(text, start); m.Success; m = Token.Match(text, m.Index + m.Length))
        {
            var word = m.Groups["word"].Value.TrimEnd('.', ',', ':', ';', '!', '?');
            if (_canonical.TryGetValue(word, out var id)) { if (!recipients.Contains(id)) recipients.Add(id); }
            else if (!unknown.Contains(word, StringComparer.OrdinalIgnoreCase)) unknown.Add(word);
        }
        return new LeadingMentions(recipients, unknown);
    }
}
