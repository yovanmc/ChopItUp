using System.Text.RegularExpressions;

namespace ChopItUp.Hub.Security;

/// <summary>Row 28 Task 2: the one place that answers "which substrings of this text could be a
/// minted bearer token" (see <c>TokenStore.NewToken</c>). Task 3's start-time sweep of
/// <c>data\host-configs\</c> and Task 6's proof that walks the whole data dir both need this, and a
/// critique pass flagged that two separate extractors would silently disagree — one definition, both
/// consumers.
///
/// A minted token is 32 random bytes, base64-encoded, padding trimmed and made URL-safe (<c>+</c>/
/// <c>/</c> replaced with <c>-</c>/<c>_</c>): always exactly <see cref="TokenLength"/> characters
/// drawn from <c>[A-Za-z0-9_-]</c>. This scans the raw text for maximal runs of that alphabet and
/// keeps only the ones exactly that long, so it needs no parser and finds a candidate in malformed
/// JSON, TOML, Markdown, or anything else just as well as in well-formed input — every real
/// host-config shape delimits the token with a quote, a brace, or the space in a <c>Bearer </c>
/// prefix, and none of those characters are in the alphabet, so they already act as boundaries
/// without any JSON- or TOML-specific handling. A <c>Bearer &lt;token&gt;</c> header value therefore
/// yields the token alone: <c>Bearer</c> is its own 6-character run, separated from the token by the
/// space, and only the run whose length matches survives the filter.</summary>
public static class TokenScan
{
    /// <summary>The exact length of every value a hub mints (32 bytes, base64url, padding stripped).
    /// A run of the alphabet that is not exactly this long is not a token, however credential-shaped
    /// it looks.</summary>
    public const int TokenLength = 43;

    private static readonly Regex AlphabetRun = new("[A-Za-z0-9_-]+", RegexOptions.Compiled);

    /// <summary>Every maximal run of the minted alphabet in <paramref name="text"/> that is exactly
    /// <see cref="TokenLength"/> characters long. Does not require <paramref name="text"/> to parse
    /// as anything — a truncated or hand-edited file still yields its candidates. Yields one entry
    /// per occurrence (not deduplicated); a caller that only cares which distinct values to check
    /// can call <c>Distinct()</c> on the result.</summary>
    public static IEnumerable<string> Candidates(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in AlphabetRun.Matches(text))
        {
            if (m.Length == TokenLength) yield return m.Value;
        }
    }
}
