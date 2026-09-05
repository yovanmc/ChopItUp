using System.Text.RegularExpressions;

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
}
