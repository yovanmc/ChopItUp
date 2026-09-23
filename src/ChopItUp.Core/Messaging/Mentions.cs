using System.Text.RegularExpressions;
using ChopItUp.Core.Model;
using ChopItUp.Core.Skills;

namespace ChopItUp.Core.Messaging;

/// <summary>Server-side mention detection, built once per roster. The shape mirrors the client's
/// highlighter (<c>participants.ts</c>): <c>@</c> + id, not followed by an optional dot and a
/// word character, so <c>@claude.</c> at the end of a sentence matches and <c>@claude-2</c> does
/// not. The server adds a lookbehind the client lacks: <c>me@opus.com</c> may highlight in the
/// browser but must never spawn. Ids are matched longest-first so a dotted id beats its prefix,
/// case-insensitively, and reported by canonical id in first-appearance order without
/// duplicates.</summary>
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

    /// <summary>Who a message addresses. Only the run of @word tokens at the start of the body counts,
    /// after an optional command prefix (the <c>/name</c> token <see cref="SlashCommands"/> recognises
    /// or the <c>phase:</c> tag <see cref="PhaseTag"/> recognises), reading any `turns:` token in the
    /// run as the exchange's turn count: once `turns:` is found the token always matches, so a
    /// malformed value (empty, too many digits, or letters stuck to the number) is reported as out of
    /// range rather than silently read as prose. Tokens are separated by ASCII whitespace (line breaks
    /// included), commas, colons or semicolons; a trailing sentence mark on a word is not part of the
    /// id. Recipients are canonical roster ids in first-appearance order without duplicates; Unknown
    /// are the leading words that matched nobody, verbatim. Everything after the first non-@ token is
    /// prose, and <see cref="Find"/> still sees it as a reference.
    /// Character classes are spelled out in ASCII (never <c>\w</c>/<c>\s</c>) so the client's twin in
    /// participants.ts, whose engine defines those classes differently, reads every body the same way.</summary>
    public sealed record LeadingMentions(IReadOnlyList<string> Recipients, IReadOnlyList<string> Unknown, TurnsToken Turns = TurnsToken.None, int TurnsValue = 0)
    {
        public static readonly LeadingMentions None = new([], []);
    }

    private static readonly Regex Token = new(@"\G[ \t\r\n\f\v,:;]*@(?<word>[A-Za-z0-9][A-Za-z0-9_.\-]*)(?![\p{L}\p{N}_.\-\uD800-\uDBFF])", RegexOptions.CultureInvariant);

    /// <summary>`turns:` inside the leading run, read in the same sticky walk as
    /// <see cref="Token"/>; the first one wins and the run goes on past it. Always matches once `turns:`
    /// is found: the digits and any trailing junk right after it are captured rather than left
    /// unmatched, so a malformed token (no digits, more than 9 of them, or a letter glued to the number)
    /// is read and refused (<see cref="TurnsToken.OutOfRange"/>) rather than breaking the walk. Letters
    /// are spelled per case so V8 needs no flag; the digit and junk classes are ASCII like every other
    /// class here.</summary>
    private static readonly Regex Turns = new(@"\G[ \t\r\n\f\v,:;]*[Tt][Uu][Rr][Nn][Ss]:[ \t]*(?<n>[0-9]*)(?<junk>[A-Za-z0-9_.\-]*)", RegexOptions.CultureInvariant);

    public LeadingMentions Leading(string body)
    {
        if (string.IsNullOrEmpty(body)) return LeadingMentions.None;
        var text = body.Replace("\r\n", "\n");
        var start = 0;
        if (SlashCommands.TryParse(text, out var command)) start = 1 + command.Name.Length;
        else if (PhaseTag.TryParse(text, out _, out var tagLength)) start = tagLength;
        var recipients = new List<string>();
        var unknown = new List<string>();
        var turns = TurnsToken.None;
        var turnsValue = 0;
        var at = start;
        while (true)
        {
            var t = Turns.Match(text, at);
            if (t.Success)
            {
                at = t.Index + t.Length;
                if (turns == TurnsToken.None)
                {
                    // Junk must be empty and n non-empty; n is capped at 9 digits before parsing so a
                    // pathologically long run of digits never reaches int.Parse's overflow path: it is
                    // simply out of range, the same as 17 or 0.
                    var n = t.Groups["n"].Value;
                    var junk = t.Groups["junk"].Value;
                    if (junk.Length == 0 && n.Length is > 0 and <= 9
                        && int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
                        && value is >= 1 and <= ExchangeCommands.MaxTurns)
                    {
                        turns = TurnsToken.Valid;
                        turnsValue = value;
                    }
                    else
                    {
                        turns = TurnsToken.OutOfRange;
                        turnsValue = 0;
                    }
                }
                continue;
            }
            var m = Token.Match(text, at);
            if (!m.Success) break;
            at = m.Index + m.Length;
            var word = m.Groups["word"].Value.TrimEnd('.', ',', ':', ';', '!', '?');
            if (_canonical.TryGetValue(word, out var id)) { if (!recipients.Contains(id)) recipients.Add(id); }
            else if (!unknown.Contains(word, StringComparer.OrdinalIgnoreCase)) unknown.Add(word);
        }
        return new LeadingMentions(recipients, unknown, turns, turnsValue);
    }
}
